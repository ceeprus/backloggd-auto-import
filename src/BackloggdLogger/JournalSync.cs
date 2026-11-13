using System.Runtime.InteropServices;
using BackloggdLogger.Core;

namespace BackloggdLogger;

// Adds play time to the journal, one day at a time, in a JournalWindow nobody sees, only while every gate holds;
// if that window ever gets focus, runs stop until the app restarts. Mark as choices go out the same way, first.
sealed class JournalSync : IDisposable
{
    readonly AppState _state;
    readonly JournalScheduler _scheduler;
    readonly StatusSaves _statuses;
    readonly List<(string GameId, StatusPick Pick, StatusNotice Notice)> _statusNotices = new();
    readonly Func<bool> _playRunning;
    readonly Func<bool> _windowOpen;
    readonly Action<GameRecord, IReadOnlyList<PlayDay>> _gaveUp;
    readonly Action<string> _changed;
    readonly Action? _paused;
    bool _pausedTold;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    bool _running;
    bool _disposed;

    internal Func<bool> ScreenBusy { get; set; } = () => Fullscreen.IsBusy() || Fullscreen.ForegroundCoversMonitor();

    internal Func<bool> AppInFront { get; set; } = ForegroundIsOurs;

    // False until the startup cache clean-up has finished with the browser profile.
    internal Func<bool> Ready { get; set; } = () => StorageCaps.Finished;

    // How one job runs; UiCheck swaps in a fake, so no WebView2 starts there.
    internal Func<JournalJob, Func<bool>, Func<int, string?, bool>, Task<JournalOutcome>> Runner { get; set; }

    internal Func<StatusJob, Func<bool>, Task<JournalOutcome>> StatusRunner { get; set; }

    // Puts back an editor cookie a cut-off run left switched (at the start, before any run).
    internal Func<EditorCookieSaved, Task<bool>> CookieRestorer { get; set; }

    readonly Func<bool> _saveChecked;

    // Tells the user a Mark as choice was saved, or that the app gave up on it.
    internal Action<GameRecord, StatusPick>? StatusSaved { get; set; }

    internal Action<GameRecord, StatusPick>? StatusFailed { get; set; }

    internal Action<GameRecord, StatusPick>? StatusExpired { get; set; }

    // The hidden window got focus once: no more runs this session.
    internal bool Paused { get; private set; }

    // saveChecked: writes the state and says whether it reached state.json; Save is never pressed
    // without that. paused: tells the user once that journal runs stopped for the session.
    public JournalSync(AppState state, Action save, Func<bool> saveChecked, Func<bool> playRunning, Func<bool> windowOpen,
        Action<GameRecord, IReadOnlyList<PlayDay>> gaveUp, Action<string> changed, Action? paused = null)
    {
        _state = state;
        _scheduler = new JournalScheduler(state, save, saveChecked);
        _statuses = new StatusSaves(state, save);
        _saveChecked = saveChecked;
        Runner = RunHiddenAsync;
        StatusRunner = RunStatusHiddenAsync;
        CookieRestorer = RestoreLeftCookieAsync;
        _paused = paused;
        _playRunning = playRunning;
        _windowOpen = windowOpen;
        _gaveUp = gaveUp;
        _changed = changed;
        _timer.Tick += (_, _) => _ = TickAsync();
        _timer.Start();
    }

    internal bool Running => _running;

    internal bool Due => _scheduler.Due;

    // A session ended, the app started or a log was saved: send what is pending at the next chance.
    public void Kick()
    {
        _scheduler.Kick();
        _statuses.Kick();
    }

    // A Mark as choice left on the close popup: saved at the next chance (a game with a match), or
    // kept until the game is matched.
    public PickKept KeepPick(string gameId, StatusPick pick) => _statuses.Keep(gameId, pick);

    // A game got its Backloggd match: a choice that waited for it can go now.
    public void KickStatus() => _statuses.Kick();

    // Open log on a give-up notice: the user takes the choice into the log box.
    public StatusPick? TakeOverPick(string gameId) => _statuses.TakeOver(gameId);

    bool ShouldStopStatus() => _disposed || Paused || Gate().Blocked;

    JournalGate Gate() => new(_playRunning(), ScreenBusy(), _windowOpen(), AppInFront());

    bool ShouldStop() => _disposed || Paused || !_state.Settings.AutoAddJournalTime || Gate().Blocked;

    internal async Task TickAsync()
    {
        if (_running || _disposed) return;
        // A cookie a cut-off run left switched goes back first, under the same gates as a run; after
        // three failed tries this session it waits for the next start and other runs go on.
        if (_state.EditorCookieToRestore is { } left && _cookieTries < MaxCookieTries && Ready() && !Gate().Blocked)
        {
            await RestoreCookieAsync(left);
            return;
        }
        // Mark as notices go out as soon as nothing is in the way, journal pass or not.
        if (_statusNotices.Count > 0 && !Gate().Blocked) TellStatus();
        foreach (var old in _statuses.Expire())
        {
            Log.Write("journal", $"{old.Id} Mark as {old.PendingPick?.Describe()} waited over a day; not saved, the user is told");
            _statusNotices.Add((old.Id, old.PendingPick!, StatusNotice.Expired));
        }
        if (!Paused && _statuses.Due && Ready() && _statuses.Next(Gate()) is { } statusJob)
        {
            await RunStatusAsync(statusJob);
            return;
        }
        if (!_scheduler.Due || Paused || !_state.Settings.AutoAddJournalTime)
        {
            // No pass going on: tell the user about given-up days once nothing is in the way.
            if (!Gate().Blocked) Tell();
            return;
        }
        if (!Ready()) return;
        var job = _scheduler.Next(Gate());
        if (job is null) return;
        _running = true;
        try
        {
            JournalOutcome outcome;
            try { outcome = await Runner(job, ShouldStop, (target, session) => _scheduler.Prepare(job, target, session)); }
            catch (Exception ex)
            {
                Log.Write("journal", $"{job.GameId} {job.Date:yyyy-MM-dd} run failed: {ex.Message}");
                outcome = JournalOutcome.Failed("error");
            }
            if (_disposed) return;
            if (outcome.Activated || outcome.Step == "activated")
            {
                Paused = true;
                Log.Write("journal", "the hidden window got focus; no more journal runs until the app starts again");
            }
            if (_scheduler.Finish(job, outcome))
                Log.Write("journal", $"{job.GameId} {job.Date:yyyy-MM-dd} not added after {Journal.MaxTries} tries");
            try { _changed(job.GameId); }
            catch (Exception ex) { Log.Write("errors", "journal changed: " + ex); }
        }
        finally { _running = false; }
    }

    const int MaxCookieTries = 3;
    int _cookieTries;

    async Task RestoreCookieAsync(EditorCookieSaved left)
    {
        _running = true;
        var done = false;
        try
        {
            done = await CookieRestorer(left);
            if (done)
            {
                _state.EditorCookieToRestore = null;
                _saveChecked();
                Log.Write("journal", $"put back the editor cookie a cut-off run left switched ({(left.Existed ? left.Value : "absent")})");
            }
        }
        catch (Exception ex) { Log.Write("journal", "editor cookie put back failed: " + ex.Message); }
        finally
        {
            _running = false;
            if (!done && ++_cookieTries >= MaxCookieTries)
                Log.Write("journal", $"editor cookie not put back after {MaxCookieTries} tries; it stays written down for the next start");
        }
    }

    // The window's cookie hooks: written to state before a switch, cleared after the put-back.
    void Hook(JournalWindow window)
    {
        window.CookieLeftSwitched = _state.EditorCookieToRestore;
        window.CookieStore = saved =>
        {
            _state.EditorCookieToRestore = saved;
            return _saveChecked();
        };
    }

    async Task RunStatusAsync(StatusJob job)
    {
        _running = true;
        try
        {
            JournalOutcome outcome;
            try { outcome = await StatusRunner(job, ShouldStopStatus); }
            catch (Exception ex)
            {
                Log.Write("journal", $"{job.GameId} Mark as run failed: {ex.Message}");
                outcome = JournalOutcome.Failed("error");
            }
            if (_disposed) return;
            if (outcome.Activated || outcome.Step == "activated")
            {
                Paused = true;
                Log.Write("journal", "the hidden window got focus; no more runs until the app starts again");
            }
            var notice = _statuses.Finish(job, outcome);
            if (notice != StatusNotice.None) _statusNotices.Add((job.GameId, job.Pick, notice));
            if (notice == StatusNotice.GaveUp) Log.Write("journal", $"{job.GameId} Mark as {job.Pick.Describe()} not saved; the user is told");
            try { _changed(job.GameId); }
            catch (Exception ex) { Log.Write("errors", "journal changed: " + ex); }
            // The game may be logged now: its play time can go too.
            if (outcome.Kind == JournalOutcomeKind.Saved) _scheduler.Kick();
        }
        finally { _running = false; }
    }

    void Tell()
    {
        TellStatus();
        TellJournal();
    }

    void TellStatus()
    {
        foreach (var (gameId, pick, notice) in _statusNotices.ToList())
        {
            _statusNotices.Remove((gameId, pick, notice));
            if (!_state.Games.TryGetValue(gameId, out var g)) continue;
            var tell = notice switch { StatusNotice.Saved => StatusSaved, StatusNotice.Expired => StatusExpired, _ => StatusFailed };
            try { tell?.Invoke(g, pick); }
            catch (Exception ex) { Log.Write("errors", "mark as notice: " + ex); }
        }
    }

    void TellJournal()
    {
        if (Paused && !_pausedTold)
        {
            _pausedTold = true;
            try { _paused?.Invoke(); }
            catch (Exception ex) { Log.Write("errors", "journal paused notice: " + ex); }
        }
        foreach (var notice in _scheduler.TakeNotices())
        {
            Log.Write("journal", $"{notice.Game.Id} told the user about {string.Join(", ", notice.Days.Select(d => $"{d.Date:yyyy-MM-dd} {d.PendingMinutes}m"))}");
            try { _gaveUp(notice.Game, notice.Days); }
            catch (Exception ex) { Log.Write("errors", "journal notice: " + ex); }
        }
    }

    async Task<JournalOutcome> RunHiddenAsync(JournalJob job, Func<bool> shouldStop, Func<int, string?, bool> prepare)
    {
        using var window = new JournalWindow();
        Hook(window);
        window.ShowHidden();
        try { return await window.RunAsync(job, shouldStop, prepare); }
        finally { window.Close(); }
    }

    async Task<JournalOutcome> RunStatusHiddenAsync(StatusJob job, Func<bool> shouldStop)
    {
        using var window = new JournalWindow();
        Hook(window);
        window.ShowHidden();
        try { return await window.RunStatusAsync(job, shouldStop); }
        finally { window.Close(); }
    }

    static async Task<bool> RestoreLeftCookieAsync(EditorCookieSaved saved)
    {
        using var window = new JournalWindow();
        window.ShowHidden();
        try { return await window.RestoreLeftCookieAsync(saved); }
        finally { window.Close(); }
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    static bool ForegroundIsOurs()
    {
        var w = GetForegroundWindow();
        if (w == IntPtr.Zero) return false;
        GetWindowThreadProcessId(w, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}

using BackloggdLogger.Core;

namespace BackloggdLogger;

sealed class GameWatcher(AppState state, ISteamSource steam, IProcessSource processes, Coordinator coordinator, ILauncherSource? launchers = null, Action? save = null) : IDisposable
{
    static readonly TimeSpan CheckpointEvery = TimeSpan.FromMinutes(1);

    // Under SessionTracker.MaxPollGap, so a relaunch inside it continues the session.
    public static TimeSpan RestartGrace { get; set; } = TimeSpan.FromSeconds(90);
    DateTime _checkpointAt;

    // Epic and GOG games seen running, by id, for the name and exe of the record made from the id.
    readonly Dictionary<string, LauncherGame> _launcherGames = new();

    readonly System.Windows.Forms.Timer _timer = new() { Interval = PollMs(state.Settings) };
    readonly SessionTracker _tracker = new();
    readonly RunningHold _hold = new();
    readonly RunningHold _playHold = new();
    readonly SeenTitles _seenTitles = new();

    // Names of emulator games as their titles gave them, for records created from an emu: id.
    readonly Dictionary<string, string> _emulatorNames = new();

    static int PollMs(AppSettings s) => s.PollSeconds * 1000;

    // How long after the last game was seen the app still counts a game as running, so an
    // app-opened window waits that long before it opens.
    TimeSpan GameHold => TimeSpan.FromSeconds(state.Settings.WindowDelayAfterGameSeconds);

    public bool AnyGameRunning { get; private set; }

    // Wider than AnyGameRunning, for journal runs: any Steam game, any tracked exe (Never ones and
    // emulators too) and any known emulator that is running.
    public bool AnyPlayRunning { get; private set; }

    // The first scan runs right away, so AnyGameRunning is already true for a game that was
    // running when the app started.
    public void Start()
    {
        _timer.Tick += (_, _) => Tick();
        Tick();
        _timer.Start();
    }

    public void SettingsChanged()
    {
        var ms = PollMs(state.Settings);
        if (_timer.Interval != ms) _timer.Interval = ms;
    }

    void Tick()
    {
        var settings = state.Settings;
        SettingsChanged();
        _tracker.MinSession = TimeSpan.FromSeconds(settings.MinSessionSeconds);
        _tracker.RestartGrace = RestartGrace;
        IReadOnlyList<SessionEvent> events;
        try
        {
            var ids = new HashSet<string>();
            var appId = steam.RunningAppId();
            if (appId != 0) ids.Add(GameIds.Steam(appId));

            var knownEmulators = settings.DetectEmulators ? processes.RunningKnownEmulators() : [];
            if (settings.DetectEmulators) coordinator.DetectEmulators(knownEmulators);

            var tracked = state.Games.Values.Where(g => g.ExePath is not null).ToList();
            var games = tracked.Where(g => !g.IsEmulator).Select(g => g.ExePath!).ToList();
            // Installed Epic and GOG games are looked for like tracked exes, minus those the user
            // tracks by that exe already.
            var installed = settings.DetectLauncherGames && launchers is not null
                ? LauncherGames.Untracked(launchers.Installed(), tracked.Select(g => g.ExePath!))
                : [];
            var running = processes.RunningExePaths(games.Concat(installed.Select(g => g.ExePath)));
            foreach (var path in games.Where(running.Contains)) ids.Add(GameIds.Exe(path));
            if (installed.Count > 0)
            {
                foreach (var game in LauncherGames.Running(installed, running, appId != 0 ? steam.InstallFolder(appId) : null))
                {
                    ids.Add(game.Id);
                    _launcherGames[game.Id] = game;
                }
            }
            var (emulatorOpen, emulatorRunning) = AddEmulatorGames(tracked.Where(g => g.IsEmulator).ToList(), ids);

            var now = DateTime.UtcNow;
            var seen = emulatorOpen
                || ids.Any(id => !state.Games.TryGetValue(id, out var g) || g.Status != GameStatus.Never);
            AnyGameRunning = _hold.Update(seen, now, GameHold);
            AnyPlayRunning = _playHold.Update(appId != 0 || running.Count > 0 || knownEmulators.Count > 0 || emulatorRunning, now, GameHold);

            events = _tracker.Poll(ids, now);
            Checkpoint(now, events.Count > 0);
        }
        catch (Exception ex)
        {
            AnyGameRunning = _hold.OnError(DateTime.UtcNow, GameHold + TimeSpan.FromMilliseconds(_timer.Interval * 2));
            // Can't tell what runs: journal runs wait.
            AnyPlayRunning = true;
            Log.Write("errors", "watcher: " + ex);
            return;
        }
        Handle(events);
    }

    // The app is closing: running sessions are recorded now, or they would be lost with the app.
    public void StopAll()
    {
        _timer.Stop();
        state.Running = null;
        var events = _tracker.StopAll();
        Handle(events);
        if (events.Count == 0) save?.Invoke();
    }

    // Sessions a killed app left in the save file, before the first poll: recent ones continue, older ones are recorded.
    public void RecoverLeftover()
    {
        if (state.Running is not { Count: > 0 } left) return;
        state.Running = null;
        _tracker.MinSession = TimeSpan.FromSeconds(state.Settings.MinSessionSeconds);
        var events = _tracker.Resume(left, DateTime.UtcNow);
        Log.Write("sessions", $"{left.Count} sessions the app didn't see end: {events.Count} recorded, the rest continue if still running");
        Handle(events);
        if (events.Count == 0) save?.Invoke();
    }

    // The running sessions go into the save when a game starts or stops (the coordinator saves then) and once a
    // minute while one runs, so a killed app loses at most a minute.
    void Checkpoint(DateTime now, bool eventsWillSave)
    {
        var live = _tracker.Snapshot();
        state.Running = live.Count == 0 ? null : live.ToList();
        if (eventsWillSave || live.Count == 0 || now - _checkpointAt < CheckpointEvery) return;
        _checkpointAt = now;
        save?.Invoke();
    }

    void Handle(IReadOnlyList<SessionEvent> events)
    {
        foreach (var raw in events)
        {
            try
            {
                SessionEvent e = raw switch
                {
                    GameStarted s => s with { StartedAt = s.StartedAt.ToLocalTime() },
                    GameStopped s => s with { StartedAt = s.StartedAt.ToLocalTime() },
                    _ => raw
                };
                Log.Write("sessions", e.ToString());
                coordinator.Handle(e, NameOf);
                // Kept on the record for its icon and tooltip; written with the record's next save.
                if (_launcherGames.TryGetValue(e.GameId, out var found) && state.Games.TryGetValue(e.GameId, out var record))
                    record.LauncherExe = found.ExePath.ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Log.Write("errors", "watcher event: " + ex);
            }
        }
    }

    // A running emulator counts as its games: each window whose title names a game adds that game's id. Open: a
    // non-Never emulator has a window, so no app window opens over a title it couldn't read. Running: any emulator runs.
    (bool Open, bool Running) AddEmulatorGames(List<GameRecord> emulators, HashSet<string> ids)
    {
        if (emulators.Count == 0) return (false, false);
        var open = false;
        var titles = processes.WindowTitles(emulators.Select(g => g.ExePath!));
        foreach (var emu in emulators)
        {
            if (!titles.TryGetValue(emu.ExePath!, out var list)) continue;
            if (emu.Status != GameStatus.Never && list.Count > 0) open = true;
            foreach (var title in list)
            {
                var hit = EmulatorTitles.Parse(emu, title);
                if (_seenTitles.FirstSight(emu.ExePath!, title))
                    Log.Write("emulators", $"{Path.GetFileName(emu.ExePath)}: \"{title}\" -> {(hit.Name is { } n ? $"\"{n}\"" : "no game")}");
                if (hit.Id is null || hit.Name is null) continue;
                ids.Add(hit.Id);
                _emulatorNames.TryAdd(hit.Id, hit.Name);
            }
        }
        return (open, titles.Count > 0);
    }

    string NameOf(string id)
    {
        if (GameIds.IsEmulatorGame(id))
            return _emulatorNames.TryGetValue(id, out var name) ? name : id[(id.IndexOf('|') + 1)..];
        if (GameIds.Launcher(id) is not null)
            return _launcherGames.TryGetValue(id, out var game) ? game.Name : id[(id.IndexOf(':') + 1)..];
        if (GameIds.IsSteam(id))
        {
            var appId = uint.Parse(id["steam:".Length..]);
            return steam.AppName(appId) ?? $"Steam app {appId}";
        }
        return Path.GetFileNameWithoutExtension(id["exe:".Length..]);
    }

    public void Dispose() => _timer.Dispose();
}

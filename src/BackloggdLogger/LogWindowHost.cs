using BackloggdLogger.Core;

namespace BackloggdLogger;

// One Backloggd window at a time. A log request comes from a button the user clicked, so it opens at once; one
// that arrives while a window is open waits, and the open window comes to front with a notice.
sealed class LogWindowHost : ILogOpener, IDisposable
{
    readonly AppState _state;
    readonly Action _save;
    readonly Queue<LogRequest> _queue = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    LogWindow? _open;

    public Coordinator? Coordinator { get; set; }
    public Action? GameChanged { get; set; }

    readonly Func<bool> _anyGameRunning;
    string? _openGameId;
    LogRequest? _openRequest;
    bool _openIsMatch;
    bool _openIsBrowse;
    Action? _nextUserWindow;

    // How a new window is shown; the test harness records it instead, so WebView2 never starts there.
    internal Action<LogWindow> ShowWindow { get; set; } = w =>
    {
        w.Show();
        w.Activate();
    };

    // SHQueryUserNotificationState; the test harness sets both answers.
    internal Func<bool> ScreenBusy { get; set; } = Fullscreen.IsBusy;

    public LogWindowHost(AppState state, Action save, Func<bool> anyGameRunning)
    {
        _state = state;
        _save = save;
        _anyGameRunning = anyGameRunning;
        _timer.Tick += (_, _) => OpenQueued(userJustClosed: false);
        _timer.Start();
    }

    internal LogWindow? OpenWindow => _open is { IsDisposed: false } w ? w : null;

    internal int Waiting => _queue.Count;

    public void Open(LogRequest request)
    {
        if (!_state.Games.ContainsKey(request.GameId)) return;
        if (OpenWindow is null)
        {
            // This answer replaces one for the same game that is still waiting.
            var others = _queue.Where(q => q.GameId != request.GameId).ToList();
            _queue.Clear();
            foreach (var q in others) _queue.Enqueue(q);
            OpenLog(request);
            return;
        }
        if (request.GameId == _openGameId && request == _openRequest)
        {
            BringToFront();
            return;
        }
        if (_queue.Any(q => q.GameId == request.GameId))
        {
            var replaced = _queue.Select(q => q.GameId == request.GameId ? request : q).ToList();
            _queue.Clear();
            foreach (var q in replaced) _queue.Enqueue(q);
        }
        else _queue.Enqueue(request);
        var name = _state.Games.TryGetValue(request.GameId, out var g) ? g.DisplayName : request.GameId;
        _open!.Notice($"Close this window to open the log for \"{name}\".");
        BringToFront();
    }

    public void OpenMatch(string gameId)
    {
        if (!_state.Games.TryGetValue(gameId, out var game)) return;
        OpenUserInitiated(_openIsMatch && _openGameId == gameId,
            $"Close this window to search Backloggd for \"{game.DisplayName}\".",
            () => OpenMatchWindow(gameId));
    }

    public void OpenBrowse() =>
        OpenUserInitiated(_openIsBrowse, "Close this window to open a new Backloggd window.", OpenBrowseWindow);

    void OpenUserInitiated(bool sameAsOpen, string notice, Action open)
    {
        if (OpenWindow is null)
        {
            _nextUserWindow = null;
            open();
            return;
        }
        if (!sameAsOpen)
        {
            _nextUserWindow = open;
            _open!.Notice(notice);
        }
        BringToFront();
    }

    void BringToFront()
    {
        if (OpenWindow is not { } w) return;
        if (w.WindowState == FormWindowState.Minimized) w.WindowState = FormWindowState.Normal;
        w.Activate();
    }

    // Requests that waited behind an open window open at once when the user closes it. A later retry from the
    // timer is the app acting on its own, so it waits while a game runs or the screen is busy.
    void OpenQueued(bool userJustClosed)
    {
        while (_open is null && _queue.Count > 0 && !ScreenBusy() && (userJustClosed || !_anyGameRunning()))
        {
            var request = _queue.Dequeue();
            if (!_state.Games.TryGetValue(request.GameId, out var game) || game.Status == GameStatus.Never) continue;
            if (request.IsStartShape && game.PendingStart is null && game.Status == GameStatus.Playing) continue;
            OpenLog(request);
        }
    }

    void OpenLog(LogRequest request)
    {
        if (!_state.Games.TryGetValue(request.GameId, out var game)) return;
        try
        {
            Show(new LogWindow(
                request,
                game,
                (slug, title) => { game.SetMatch(slug, title); _save(); GameChanged?.Invoke(); },
                () => Coordinator?.Answer(game.Id, PromptAnswer.Never, DateOnly.FromDateTime(DateTime.Today)),
                (status, playing, found) =>
                {
                    if (found) Coordinator?.Saved(game.Id, status, playing);
                    else Coordinator?.SavedFallback(game.Id, request);
                }),
                game.Id, request, isMatch: false);
        }
        catch (Exception ex)
        {
            Log.Write("errors", $"couldn't open log window for {request.GameId}: {ex}");
            Clear();
        }
    }

    void OpenMatchWindow(string gameId)
    {
        if (!_state.Games.TryGetValue(gameId, out var game)) return;
        try
        {
            Show(new LogWindow(
                null,
                game,
                (slug, title) => { game.SetMatch(slug, title); _save(); GameChanged?.Invoke(); },
                () =>
                {
                    Coordinator?.Answer(game.Id, PromptAnswer.Never, DateOnly.FromDateTime(DateTime.Today));
                    GameChanged?.Invoke();
                },
                (_, _, _) => { }),
                game.Id, null, isMatch: true);
        }
        catch (Exception ex)
        {
            Log.Write("errors", $"couldn't open match window for {gameId}: {ex}");
            Clear();
        }
    }

    void OpenBrowseWindow()
    {
        try
        {
            Show(new LogWindow(null, null, (_, _) => { }, () => { }, (_, _, _) => { }), null, null, isMatch: false);
            _openIsBrowse = true;
        }
        catch (Exception ex)
        {
            Log.Write("errors", "couldn't open Backloggd window: " + ex);
            Clear();
        }
    }

    void Show(LogWindow window, string? gameId, LogRequest? request, bool isMatch)
    {
        _open = window;
        _openGameId = gameId;
        _openRequest = request;
        _openIsMatch = isMatch;
        _openIsBrowse = false;
        window.BlockAds = _state.Settings.BlockAds;
        window.FormClosed += (_, _) =>
        {
            if (_open != window) return;
            Clear();
            var next = _nextUserWindow;
            _nextUserWindow = null;
            next?.Invoke();
            OpenQueued(userJustClosed: true);
        };
        ShowWindow(window);
    }

    void Clear()
    {
        _open = null;
        _openGameId = null;
        _openRequest = null;
        _openIsMatch = false;
        _openIsBrowse = false;
    }

    public void Dispose() => _timer.Dispose();
}

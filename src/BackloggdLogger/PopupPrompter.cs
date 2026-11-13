using BackloggdLogger.Core;

namespace BackloggdLogger;

sealed class PopupPrompter : IPrompter
{
    static readonly TimeSpan NoticeTimeout = TimeSpan.FromSeconds(10);

    readonly Action<string, PromptAnswer, DateOnly> _answer;
    readonly Action<string, PlayedStatus?, bool?, bool?, bool?> _markAs;
    readonly Func<AppSettings> _settings;

    // Popups on screen again from the Missed popups window, by their entry's id.
    readonly Dictionary<string, PromptPopup> _again = new();

    public PopupPrompter(Action<string, PromptAnswer, DateOnly> answer, Action<string, PlayedStatus?, bool?, bool?, bool?> markAs,
        Func<AppSettings>? settings = null)
    {
        _answer = answer;
        _markAs = markAs;
        _settings = settings ?? (() => new AppSettings());
    }

    AppSettings Settings => _settings();

    // Takes a popup that went away unanswered (it timed out, a fullscreen game hid it, or the app
    // closed it) into the missed queue.
    public Action<MissedPopup>? Missed { get; set; }

    // Takes an entry out of the queue: its popup was shown again and the user answered or closed it.
    public Action<string>? Forget { get; set; }

    // What a notice's first button does: the game's log window, or the folder of a file.
    public Action<string>? OpenLog { get; set; }

    public Action<string>? OpenFolder { get; set; }

    public Action<string>? OpenBackloggd { get; set; }

    // A Mark as choice the close popup was left with: the app saves it by itself.
    public Action<string, StatusPick>? KeepPick { get; set; }

    internal static StatusPick Pick(MarkAsSelection s) => new(s.Status, s.Playing, s.Backlog, s.Wishlist);

    public void MarkAs(string gameId, PlayedStatus? status, bool? playing, bool? backlog, bool? wishlist)
    {
        try { _markAs(gameId, status, playing, backlog, wishlist); }
        catch (Exception ex) { Log.Write("errors", "popup mark as: " + ex); }
    }

    public void AskStart(GameRecord g, DateTime startedAt) =>
        Show(new MissedPopup
        {
            GameId = g.Id, Kind = MissedKind.Start, Title = $"{g.DisplayName} started", Body = "Add it to Backloggd as Playing?",
            SessionStart = DateOnly.FromDateTime(startedAt),
        }, again: false);

    public void AskAfterClose(GameRecord g, TimeSpan played, DateTime startedAt) =>
        Show(new MissedPopup
        {
            GameId = g.Id, Kind = MissedKind.AfterClose, Title = Format.Closed(g.DisplayName, played), Body = "Add it to Backloggd?",
            SessionStart = DateOnly.FromDateTime(startedAt),
        }, again: false);

    public void AskStatus(GameRecord g, TimeSpan played) =>
        Show(new MissedPopup
        {
            GameId = g.Id, Kind = MissedKind.Status, Title = Format.Closed(g.DisplayName, played), Body = "Update your log?",
            SessionStart = DateOnly.FromDateTime(DateTime.Today),
        }, again: false);

    public void Notify(string text) => Notify(text, MissedAction.None, null);

    // A notice about a file, with Open folder before Close.
    public void NotifyFolder(string text, string file) => Notify(text, MissedAction.OpenFolder, file);

    // The first sentence is the title and the rest the body, so a notice reads like the other popups.
    void Notify(string text, MissedAction action, string? path)
    {
        var (title, body) = SplitNotice(text);
        Show(new MissedPopup { Kind = MissedKind.Notice, Title = title, Body = body, Action = action, ActionPath = path }, again: false);
    }

    // Play time the journal run gave up on, with Open log before Close. The game's name stays in the
    // title whatever it holds ("Dr. Mario"), so this one is not split at a sentence.
    public void NotifyJournalFailed(GameRecord g, string body) =>
        Show(new MissedPopup { GameId = g.Id, Kind = MissedKind.Notice, Title = JournalFailedTitle(g.DisplayName), Body = body, Action = MissedAction.OpenLog }, again: false);

    internal static string JournalFailedTitle(string game) => $"Couldn't add play time to Backloggd for {game}";

    public void NotifySignIn() =>
        Show(new MissedPopup { Kind = MissedKind.Notice, Title = SignInTitle, Body = SignInBody, Action = MissedAction.SignIn }, again: false);

    internal const string SignInTitle = "Sign in to Backloggd";

    internal const string SignInBody = "Play time and Mark as choices are saved to Backloggd once you're signed in.";

    public void NotifyUpdate(Version latest, Version current, string url) =>
        Show(new MissedPopup { Kind = MissedKind.Notice, Title = UpdateTitle(latest), Body = UpdateBody(current), Action = MissedAction.OpenUrl, ActionPath = url }, again: false);

    internal static string UpdateTitle(Version latest) => $"Backloggd Logger {latest.ToString(3)} is available";

    internal static string UpdateBody(Version current) => $"You have {current.ToString(3)}.";

    public void NotifyJournalPaused() =>
        Show(new MissedPopup { Kind = MissedKind.Notice, Title = JournalPausedTitle, Body = JournalPausedBody }, again: false);

    internal const string JournalPausedTitle = "Stopped adding play time to Backloggd";

    internal const string JournalPausedBody = "The app's hidden Backloggd window got focus. Play time waits and is added after the app restarts.";

    internal static (string Title, string Body) SplitNotice(string text)
    {
        var t = text.Trim();
        var i = t.IndexOf(". ", StringComparison.Ordinal);
        return i < 0 ? (t, "") : (t[..i], t[(i + 2)..].Trim());
    }

    // Shows a missed popup again: the same text and buttons, with no countdown. Answering it works
    // as the first time and takes it out of the queue. One popup per entry at a time.
    public void ShowAgain(MissedPopup missed)
    {
        if (ShownAgain(missed.Id)) return;
        Show(missed, again: true);
    }

    internal bool ShownAgain(string id) => _again.TryGetValue(id, out var open) && !open.IsDisposed;

    // Every popup is built from its description, the first time and when it is shown again.
    void Show(MissedPopup m, bool again)
    {
        var settings = Settings;
        var id = m.GameId ?? "";
        var session = m.SessionStart ?? DateOnly.FromDateTime(DateTime.Today);
        var picked = false;
        Action<bool> ended = again
            ? byUser =>
            {
                _again.Remove(m.Id);
                if (byUser) Forget?.Invoke(m.Id);
            }
            : byUser =>
            {
                // A popup whose Mark as choice was kept was acted on, so it isn't missed.
                if (!byUser && !picked) Missed?.Invoke(m);
            };
        (string, Action?) Button(string label, PromptAnswer answer) => (label, () => Answer(id, answer, session));

        PromptPopup popup;
        switch (m.Kind)
        {
            case MissedKind.Start:
                // A popup that timed out behind a fullscreen game gives the question back (Unseen),
                // so it is asked again when the game closes.
                popup = new PromptPopup(m.Title, m.Body,
                    new[] { Button("Add as Playing", PromptAnswer.Yes), Button("Not now", PromptAnswer.NotNow), Button("Never ask again", PromptAnswer.Never) },
                    again ? null : TimeSpan.FromSeconds(settings.StartPopupSeconds), again ? null : Fullscreen.IsBusy,
                    again ? null : sawBusy =>
                    {
                        if (sawBusy) _answer(id, PromptAnswer.Unseen, session);
                    },
                    corner: settings.PopupCorner, onEnded: ended);
                break;
            case MissedKind.AfterClose:
                popup = new PromptPopup(m.Title, m.Body,
                    new[]
                    {
                        Button("Playing", PromptAnswer.AddPlaying), Button("Finished", PromptAnswer.AddFinished),
                        Button("Not now", PromptAnswer.NotNow), Button("Never ask again", PromptAnswer.Never),
                    },
                    null, corner: settings.PopupCorner, onEnded: ended);
                break;
            case MissedKind.Status:
                popup = new PromptPopup(m.Title, m.Body,
                    new[] { Button("Skip", PromptAnswer.Skip), Button("Never ask again", PromptAnswer.Never) },
                    again ? null : TimeSpan.FromSeconds(settings.ClosePopupSeconds),
                    openLog: choice =>
                    {
                        if (choice is null) Answer(id, PromptAnswer.OpenLog, session);
                        else MarkAs(id, choice.Status, choice.Playing, choice.Backlog, choice.Wishlist);
                    },
                    corner: settings.PopupCorner, onEnded: ended,
                    keepPick: choice =>
                    {
                        picked = true;
                        Run(KeepPick, id, Pick(choice));
                    });
                break;
            default:
                var buttons = new List<(string, Action?)>();
                if (m.Action == MissedAction.OpenLog && m.GameId is { } game) buttons.Add(("Open log", () => Run(OpenLog, game)));
                else if (m.Action == MissedAction.OpenFolder && m.ActionPath is { } file) buttons.Add(("Open folder", () => Run(OpenFolder, file)));
                else if (m.Action == MissedAction.SignIn) buttons.Add(("Sign in", () => Run(OpenBackloggd, "")));
                else if (m.Action == MissedAction.OpenUrl && m.ActionPath is { } url) buttons.Add(("Download", () => Run(SettingsForm.OpenUrl, Releases.SafeUrl(url))));
                buttons.Add(("Close", null));
                popup = new PromptPopup(m.Title, m.Body, buttons, again ? null : NoticeTimeout, corner: settings.PopupCorner, notice: true, onEnded: ended);
                break;
        }
        if (again) _again[m.Id] = popup;
        popup.Show();
    }

    static void Run(Action<string>? action, string arg)
    {
        try { action?.Invoke(arg); }
        catch (Exception ex) { Log.Write("errors", "notice action: " + ex); }
    }

    static void Run(Action<string, StatusPick>? action, string gameId, StatusPick pick)
    {
        try { action?.Invoke(gameId, pick); }
        catch (Exception ex) { Log.Write("errors", "keep pick: " + ex); }
    }

    static readonly TimeSpan SavedTimeout = TimeSpan.FromSeconds(5);

    // A Mark as choice the app saved: the game as the title, what was saved as the body, 5 s, Close
    // only. Not kept in Missed popups: nothing is left to do.
    public void NotifyStatusSaved(GameRecord g, StatusPick pick) =>
        new PromptPopup(g.DisplayName, pick.SavedBody(), new (string, Action?)[] { ("Close", null) }, SavedTimeout,
            corner: Settings.PopupCorner, notice: true).Show();

    // A Mark as choice the app gave up on, with Open log (the box opens with that choice in it).
    public void NotifyStatusFailed(GameRecord g, StatusPick pick) =>
        Show(new MissedPopup { GameId = g.Id, Kind = MissedKind.Notice, Title = pick.FailedTitle(g.DisplayName), Body = StatusFailedBody, Action = MissedAction.OpenLog }, again: false);

    internal const string StatusFailedBody = "Open log to save it in the log box.";

    // A choice that waited more than a day: not saved, since the log may have changed since.
    public void NotifyStatusExpired(GameRecord g, StatusPick pick) =>
        Show(new MissedPopup { GameId = g.Id, Kind = MissedKind.Notice, Title = pick.ExpiredTitle(g.DisplayName), Body = StatusExpiredBody, Action = MissedAction.OpenLog }, again: false);

    internal const string StatusExpiredBody = "It waited more than a day, so your log may have changed. Open log to save it there.";

    // A choice for a game with no Backloggd match yet: kept, and saved once the game is found.
    public void NotifyPickUnmatched(GameRecord g, StatusPick pick) =>
        Show(new MissedPopup { GameId = g.Id, Kind = MissedKind.Notice, Title = PickUnmatchedTitle(g.DisplayName), Body = PickUnmatchedBody(pick), Action = MissedAction.OpenLog }, again: false);

    internal static string PickUnmatchedTitle(string game) => $"{game} isn't matched on Backloggd yet";

    internal static string PickUnmatchedBody(StatusPick pick) => $"{pick.Describe()} is kept. Open log to find the game, and it's saved after that.";

    void Answer(string gameId, PromptAnswer answer, DateOnly session)
    {
        try { _answer(gameId, answer, session); }
        catch (Exception ex) { Log.Write("errors", "popup: " + ex); }
    }
}

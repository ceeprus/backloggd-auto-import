namespace BackloggdLogger.Core;

public interface IPrompter
{
    void AskStart(GameRecord g, DateTime startedAt);
    void AskAfterClose(GameRecord g, TimeSpan played, DateTime startedAt);
    void AskStatus(GameRecord g, TimeSpan played);
    void Notify(string text);
}

// Every request comes from a popup button the user clicked, so the window opens now, even while
// a game runs (one window at a time; a second request waits for the open one to close).
public interface ILogOpener
{
    void Open(LogRequest request);
}

public sealed class Coordinator(AppState state, Action save, IPrompter prompter, ILogOpener opener, Func<DateOnly> today, Func<DateTime>? now = null)
{
    DateTime Now() => now?.Invoke() ?? DateTime.Now;

    // Raised with the game's id after its record changed, so open windows show the new values. It comes after the
    // question or log request went out and a throwing handler is skipped, so neither is ever lost.
    public event Action<string>? Changed;

    // Raised with the game's id when it may have play time to add to the journal: a session ended,
    // or a save made the game one that is logged on Backloggd.
    public event Action<string>? JournalDue;

    void Raise(string id) => Raise(Changed, id);

    static void Raise(Action<string>? handlers, string id)
    {
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try { ((Action<string>)h)(id); }
            catch (Exception) { }
        }
    }

    // The record for id, created or renamed when needed; changed is true when it was.
    GameRecord Ensure(string id, Func<string, string> nameOf, out bool changed)
    {
        changed = false;
        if (!state.Games.TryGetValue(id, out var g))
        {
            g = new GameRecord { Id = id, DisplayName = nameOf(id), EmulatorExe = GameIds.EmulatorExe(id), AddedAt = Now() };
            state.Games[id] = g;
            save();
            changed = true;
        }
        else if (g.DisplayName.StartsWith("Steam app ", StringComparison.Ordinal))
        {
            var name = nameOf(id);
            if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith("Steam app ", StringComparison.Ordinal))
            {
                g.DisplayName = name;
                save();
                changed = true;
            }
        }
        return g;
    }

    // Adds a record for each running known emulator that has none; any existing record (treated
    // as one game, Never, renamed) is left as the user set it.
    public void DetectEmulators(IEnumerable<string> exePaths)
    {
        if (!state.Settings.DetectEmulators) return;
        var added = new List<string>();
        foreach (var path in exePaths)
        {
            var id = GameIds.Exe(path);
            if (state.Games.ContainsKey(id) || state.Games.Values.Any(g => string.Equals(g.ExePath, path, StringComparison.OrdinalIgnoreCase))) continue;
            if (EmulatorTitles.Detect(Path.GetFileName(path)) is not { } kind) continue;
            state.Games[id] = new GameRecord { Id = id, DisplayName = EmulatorTitles.Name(kind), ExePath = path.ToLowerInvariant(), Asked = true, AddedAt = Now() };
            added.Add(id);
        }
        if (added.Count == 0) return;
        save();
        foreach (var id in added) Raise(id);
    }

    public void Handle(SessionEvent e, Func<string, string> nameOf)
    {
        var g = Ensure(e.GameId, nameOf, out var changed);
        var journal = false;
        // An emulator's games are asked about one by one; the emulator itself never is.
        if (g.IsEmulator)
        {
            if (changed) Raise(g.Id);
            return;
        }
        try
        {
            switch (e)
            {
                case GameStarted s:
                    // Always ask at start: a loading game often reports a busy screen. A popup that times out unseen
                    // gives the question back (PromptAnswer.Unseen), so it is asked again when the game closes.
                    if (PromptPolicy.OnStart(g, state.PromptsPaused, state.Settings.AskOnStart) == StartPrompt.AskAdd)
                    {
                        prompter.AskStart(g, s.StartedAt);
                        g.Asked = true;
                        save();
                        changed = true;
                    }
                    break;

                case GameStopped s:
                    g.LastSessionMinutes = PlayHistory.Minutes(s.Duration);
                    g.AddPlay(DateOnly.FromDateTime(s.StartedAt), g.LastSessionMinutes, forJournal: state.Settings.AutoAddJournalTime);
                    save();
                    changed = true;
                    journal = true;
                    switch (PromptPolicy.OnClose(g, state.PromptsPaused, state.Settings.AskOnClose))
                    {
                        case ClosePrompt.AskAddAfterClose:
                            prompter.AskAfterClose(g, s.Duration, s.StartedAt);
                            g.Asked = true;
                            save();
                            break;
                        case ClosePrompt.AskStatus:
                            prompter.AskStatus(g, s.Duration);
                            break;
                    }
                    break;
            }
        }
        finally
        {
            if (changed) Raise(g.Id);
            if (journal) Raise(JournalDue, g.Id);
        }
    }

    public void Answer(string gameId, PromptAnswer answer, DateOnly sessionStart)
    {
        if (!state.Games.TryGetValue(gameId, out var g)) return;
        // A later Skip or Never ask again replaces a Mark as choice still waiting to be saved.
        if (answer is PromptAnswer.Skip or PromptAnswer.Never) g.DropPick();
        var request = Actions.Apply(g, answer, sessionStart, today());
        // A game set to Never takes its missed popups with it.
        state.PruneMissed(Now());
        save();
        try
        {
            if (request is not null) opener.Open(request);
        }
        finally { Raise(g.Id); }
    }

    public void MarkAs(string gameId, PlayedStatus? status, bool? playing, bool? backlog, bool? wishlist)
    {
        if (!state.Games.TryGetValue(gameId, out var g)) return;
        opener.Open(Actions.MarkAs(g, status, playing, backlog, wishlist, today()));
    }

    public void Saved(string gameId, string? status, bool playingOn)
    {
        if (!state.Games.TryGetValue(gameId, out var g)) return;
        // The user saved the log themselves: a waiting Mark as choice must not overwrite it.
        g.DropPick();
        Actions.OnSavedState(g, status, playingOn);
        save();
        Raise(g.Id);
        Raise(JournalDue, g.Id);
    }

    public void SavedFallback(string gameId, LogRequest request)
    {
        if (!state.Games.TryGetValue(gameId, out var g)) return;
        g.DropPick();
        Actions.OnSavedFallback(g, request);
        save();
        Raise(g.Id);
        Raise(JournalDue, g.Id);
    }
}

using System.Globalization;
using System.Text.Json.Serialization;

namespace BackloggdLogger.Core;

public enum GameStatus { Unknown, Playing, Finished, Dropped, Never }

public sealed class GameRecord
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Slug { get; set; }

    // The game's title on Backloggd as the match found it ("Hollow Knight (2017)"), shown instead
    // of the slug when known.
    public string? BackloggdTitle { get; set; }
    public GameStatus Status { get; set; }
    public DateOnly? PendingStart { get; set; }

    // A Mark as choice from the close popup that waits to be saved to Backloggd by the app; the
    // failed tries for it, and whether the app gave up (then the user was told and can save it by hand).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public StatusPick? PendingPick { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PickTries { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PickGaveUp { get; set; }

    // When the choice was made; one older than a day isn't saved (the log may have changed since).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime PendingPickAt { get; set; }

    // The choice is no longer the user's latest word: they answered another popup for the game, set
    // it to Never, or saved its log themselves.
    public void DropPick()
    {
        PendingPick = null;
        PickTries = 0;
        PickGaveUp = false;
        PendingPickAt = default;
    }
    public int LastSessionMinutes { get; set; }
    public string? ExePath { get; set; }

    // A game seen in an emulator's window title: the emulator's exe (lowercased). ExePath stays
    // null, so the game itself is never looked for as a process.
    public string? EmulatorExe { get; set; }

    // A game found through the Epic or GOG install list: its exe (lowercased), for the row's icon and tooltip.
    // ExePath stays null: the game is matched through the install list, not tracked as an exe.
    public string? LauncherExe { get; set; }

    // A tracked exe that runs many games. null follows the exe name (EmulatorTitles.Detect); true
    // or false is the choice made in Tracked games.
    public bool? TreatAsEmulator { get; set; }

    // The title format to read, when the exe name doesn't say (otherwise Detect, then Generic).
    public EmulatorKind? EmulatorKindOverride { get; set; }

    [JsonIgnore]
    public bool IsEmulator => ExePath is not null && (TreatAsEmulator ?? EmulatorTitles.Detect(Path.GetFileName(ExePath)) is not null);

    public bool Asked { get; set; }

    // When the app first saw the game (local time). MinValue when unknown: a record from before
    // the field existed gets its first session day or the state file's time on load (FillAddedAt).
    public DateTime AddedAt { get; set; }

    public void FillAddedAt(DateTime? fallback)
    {
        if (AddedAt != DateTime.MinValue) return;
        if (_sessions.Count > 0) AddedAt = _sessions.Min(s => s.Date).ToDateTime(TimeOnly.MinValue);
        else if (fallback is { } f) AddedAt = f;
    }

    List<PlayDay> _sessions = new();

    // One entry per local day, in date order; sessions on the same day are summed.
    public List<PlayDay> Sessions
    {
        get => _sessions;
        set => _sessions = value ?? new();
    }

    [JsonIgnore]
    public int TotalMinutes => _sessions.Sum(s => s.Minutes);

    public void SetMatch(string slug, string? title)
    {
        if (title is not null) BackloggdTitle = title;
        else if (slug != Slug) BackloggdTitle = null;
        Slug = slug;
    }

    // forJournal: the minutes wait to be added to the Backloggd journal. Otherwise they count as
    // handled at once, so turning the switch on later never sends play from before.
    public void AddPlay(DateOnly date, int minutes, bool forJournal = false)
    {
        if (minutes <= 0) return;
        var i = _sessions.FindIndex(s => s.Date >= date);
        PlayDay day;
        if (i >= 0 && _sessions[i].Date == date) day = _sessions[i];
        else
        {
            day = new PlayDay { Date = date };
            _sessions.Insert(i < 0 ? _sessions.Count : i, day);
        }
        day.Minutes += minutes;
        if (!forJournal) day.PushedMinutes += minutes;
    }

    // Days with minutes still to add to the journal, oldest first; a day given up on is left out.
    [JsonIgnore]
    public IReadOnlyList<PlayDay> PendingJournal => _sessions.Where(d => d.PendingMinutes > 0 && !d.GaveUp).ToList();

    [JsonIgnore]
    public int SentJournalMinutes => _sessions.Sum(s => s.SentMinutes);
}

public sealed class PlayDay
{
    public DateOnly Date { get; set; }
    public int Minutes { get; set; }

    // Minutes that need nothing more: sent to the journal, or never meant for it (play from before
    // the feature, or while its switch was off).
    public int PushedMinutes { get; set; }

    // Minutes the app added to the journal itself (the Time played tooltip).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SentMinutes { get; set; }

    // Failed runs for this day; at Journal.MaxTries the app stops and says so once.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int JournalTries { get; set; }

    // A save that was sent but never confirmed: the session total it set and the minutes it added.
    // The next run adds nothing when the session already shows that total.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? TriedTotal { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TriedMinutes { get; set; }

    // The session that save was for ("new" for one the run made), so a rerun checks the same one.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? TriedSession { get; set; }

    // A run found no session it could add to (an empty day, several sessions). The first time can
    // be a calendar that had not loaded yet, so only a second such run gives up.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UnsupportedOnce { get; set; }

    // The pending minutes the user was last told to add by hand; more play that day tells again.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ToldMinutes { get; set; }

    [JsonIgnore]
    public int PendingMinutes => Math.Max(0, Minutes - PushedMinutes);

    [JsonIgnore]
    public bool GaveUp => JournalTries >= Journal.MaxTries;
}

public static class PlayHistory
{
    public static int Minutes(TimeSpan duration) => (int)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero);
}

public enum PopupCorner { BottomRight, TopRight }

public sealed record AppSettings
{
    public int MinSessionSeconds { get; init; } = 30;
    public int StartPopupSeconds { get; init; } = 10;
    public int ClosePopupSeconds { get; init; } = 10;
    public bool AskOnStart { get; init; } = true;
    public bool AskOnClose { get; init; } = true;
    public int WindowDelayAfterGameSeconds { get; init; } = 10;
    public int PollSeconds { get; init; } = 2;
    public PopupCorner PopupCorner { get; init; } = PopupCorner.BottomRight;

    // After a game closes, its play time is added to that day's session in the Backloggd journal.
    public bool AutoAddJournalTime { get; init; } = true;

    // The visible Backloggd window blocks ad and tracking hosts and hides the empty ad slots. Off
    // by default, so the site earns from its ads.
    public bool BlockAds { get; init; }

    // Known emulators (EmulatorTitles.Detect) that are running get a tracked record on their own.
    public bool DetectEmulators { get; init; } = true;

    // Games installed by the Epic Games Launcher or GOG Galaxy count as running games while their
    // exe runs, without being tracked by hand first.
    public bool DetectLauncherGames { get; init; } = true;

    // How the Tracked games window shows its list (emulators, columns, sort).
    public TrackedListView TrackedList { get; init; } = new();

    public AppSettings Normalized() => this with
    {
        TrackedList = (TrackedList ?? new()).Normalized(),
        MinSessionSeconds = SettingRanges.MinSession.Clamp(MinSessionSeconds),
        StartPopupSeconds = SettingRanges.StartPopup.Clamp(StartPopupSeconds),
        ClosePopupSeconds = SettingRanges.ClosePopup.Clamp(ClosePopupSeconds),
        WindowDelayAfterGameSeconds = SettingRanges.WindowDelay.Clamp(WindowDelayAfterGameSeconds),
        PollSeconds = SettingRanges.Poll.Clamp(PollSeconds),
        PopupCorner = Enum.IsDefined(PopupCorner) ? PopupCorner : PopupCorner.BottomRight,
    };
}

// The columns of the Tracked games list, in their order on screen.
public enum TrackedColumn { Icon, Name, Status, TimePlayed, Added, Backloggd, Source }

// A set of columns: bit n is TrackedColumn n.
[Flags]
public enum TrackedColumns
{
    None = 0,
    Icon = 1 << TrackedColumn.Icon,
    Name = 1 << TrackedColumn.Name,
    Status = 1 << TrackedColumn.Status,
    TimePlayed = 1 << TrackedColumn.TimePlayed,
    Added = 1 << TrackedColumn.Added,
    Backloggd = 1 << TrackedColumn.Backloggd,
    Source = 1 << TrackedColumn.Source,
}

public sealed record TrackedListView
{
    const TrackedColumns All = TrackedColumns.Icon | TrackedColumns.Name | TrackedColumns.Status | TrackedColumns.TimePlayed
        | TrackedColumns.Added | TrackedColumns.Backloggd | TrackedColumns.Source;

    // Emulator rows (the emulator exe itself) are hidden unless this is on; their games always show.
    public bool ShowEmulators { get; init; }

    public TrackedColumns HiddenColumns { get; init; }

    // Columns the user has shown or hidden themselves. Any other column follows its default, so a
    // default can change without undoing a choice.
    public TrackedColumns ChosenColumns { get; init; }

    // Hidden until the user shows them.
    const TrackedColumns HiddenByDefault = TrackedColumns.Status;

    public TrackedColumn SortColumn { get; init; } = TrackedColumn.Added;

    public bool SortDescending { get; init; } = true;

    public static TrackedColumns Bit(TrackedColumn c) => (TrackedColumns)(1 << (int)c);

    public bool Shows(TrackedColumn c) =>
        (HiddenColumns & Bit(c)) == 0 && ((ChosenColumns & Bit(c)) != 0 || (HiddenByDefault & Bit(c)) == 0);

    // Name is the row's identity and is never hidden.
    public TrackedListView Hide(TrackedColumn c) =>
        c == TrackedColumn.Name ? this : this with { HiddenColumns = HiddenColumns | Bit(c), ChosenColumns = ChosenColumns | Bit(c) };

    public TrackedListView Show(TrackedColumn c) => this with { HiddenColumns = HiddenColumns & ~Bit(c), ChosenColumns = ChosenColumns | Bit(c) };

    // The icon is a picture with nothing to order by.
    public static bool CanSort(TrackedColumn c) => c != TrackedColumn.Icon && Enum.IsDefined(c);

    public TrackedListView Normalized() => this with
    {
        HiddenColumns = HiddenColumns & All & ~TrackedColumns.Name,
        ChosenColumns = ChosenColumns & All,
        SortColumn = CanSort(SortColumn) ? SortColumn : TrackedColumn.Added,
    };
}

public readonly record struct SettingRange(int Min, int Max)
{
    public bool Contains(int v) => v >= Min && v <= Max;

    public int Clamp(int v) => Math.Clamp(v, Min, Max);

    public string Hint => $"Use {Min} to {Max} seconds.";

    public int? Parse(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && Contains(v) ? v : null;

    public string? Check(string? text) => Parse(text) is null ? Hint : null;
}

public static class SettingRanges
{
    public static readonly SettingRange MinSession = new(10, 600);
    public static readonly SettingRange StartPopup = new(5, 120);
    public static readonly SettingRange ClosePopup = new(5, 120);
    public static readonly SettingRange WindowDelay = new(0, 120);
    public static readonly SettingRange Poll = new(1, 30);
}

public sealed class AppState
{
    public Dictionary<string, GameRecord> Games { get; set; } = new();
    public bool PromptsPaused { get; set; }

    // The newest release the user was told about, so each version is announced once.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? UpdateNotified { get; set; }

    // False in a file written before the journal existed; see UpgradeJournal.
    public bool JournalBaselined { get; set; }

    // The owner's editor-mode cookie, written down before a run switches it to quick and cleared
    // once the run has put it back. Still set at the next start: a run was cut off, so it is put back then.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EditorCookieSaved? EditorCookieToRestore { get; set; }

    // Play recorded before the journal existed is never sent: every day counts as handled. The
    // switch was stored but could not be turned on then, so its false was no choice and turns on.
    public void UpgradeJournal()
    {
        if (!JournalBaselined)
        {
            foreach (var g in Games.Values)
                foreach (var d in g.Sessions)
                    d.PushedMinutes = Math.Max(d.PushedMinutes, d.Minutes);
            Settings = Settings with { AutoAddJournalTime = true };
            JournalBaselined = true;
        }
        RetryAfterUpgrade();
    }

    // The journal format the stored days were last tried with; see RetryAfterUpgrade.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int JournalVersion { get; set; }

    // Once per new journal format: days an older build gave up on are tried again. Only try counts reset; what
    // was sent and any unconfirmed save's total stay, so nothing can be added twice.
    void RetryAfterUpgrade()
    {
        if (JournalVersion >= Journal.FormatVersion) return;
        foreach (var g in Games.Values)
            // A day with an unconfirmed save stays given up: its notice asked the user to check it.
            foreach (var d in g.Sessions.Where(d => d.Minutes > d.PushedMinutes && d.TriedTotal is null))
            {
                d.JournalTries = 0;
                d.UnsupportedOnce = false;
            }
        JournalVersion = Journal.FormatVersion;
    }

    MissedPopups _missed = new();

    // Popups that went away unanswered, for the Missed popups window.
    public MissedPopups MissedPopups
    {
        get => _missed;
        set => _missed = value ?? new();
    }

    // Records a popup nobody answered. Nothing is kept while popups are paused, or for a game that
    // is gone or set to Never.
    public MissedPopup? RecordMissed(MissedPopup popup, DateTime now)
    {
        if (PromptsPaused) return null;
        if (popup.GameId is { } id && (!Games.TryGetValue(id, out var g) || g.Status == GameStatus.Never)) return null;
        _missed.Add(popup, now);
        return popup;
    }

    public void PruneMissed(DateTime now) => _missed.Prune(Games, now);

    AppSettings _settings = new();

    public AppSettings Settings
    {
        get => _settings;
        set => _settings = (value ?? new()).Normalized();
    }
}

public static class GameIds
{
    public static string Steam(uint appId) => $"steam:{appId}";
    public static string Exe(string path) => "exe:" + path.ToLowerInvariant();
    public static bool IsSteam(string id) => id.StartsWith("steam:", StringComparison.Ordinal);

    public static string Epic(string appName) => "epic:" + appName;
    public static string Gog(string gameId) => "gog:" + gameId;
    public static bool IsEpic(string id) => id.StartsWith("epic:", StringComparison.Ordinal);
    public static bool IsGog(string id) => id.StartsWith("gog:", StringComparison.Ordinal);
    public static string BattleNet(string uid) => "bnet:" + uid;
    public static bool IsBattleNet(string id) => id.StartsWith("bnet:", StringComparison.Ordinal);

    // The launcher a game was found through, as the Source column names it; null for the rest.
    public static string? Launcher(string id) => IsEpic(id) ? "Epic" : IsGog(id) ? "GOG" : IsBattleNet(id) ? "Battle.net" : null;

    // "emu:<exe path>|<name>": the same game in the same emulator is one id across runs, whatever
    // its title's case or punctuation. A Windows path never contains '|'.
    public static string Emulator(string exePath, string gameName) => $"emu:{exePath.ToLowerInvariant()}|{NormalizeName(gameName)}";

    public static bool IsEmulatorGame(string id) => id.StartsWith("emu:", StringComparison.Ordinal);

    public static string? EmulatorExe(string id)
    {
        if (!IsEmulatorGame(id)) return null;
        var bar = id.IndexOf('|');
        return bar < 0 ? null : id["emu:".Length..bar];
    }

    public static string NormalizeName(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

public enum PlayedStatus { Played, Completed, Retired, Shelved, Abandoned }

public sealed record LogRequest(string GameId, PlayedStatus? Status, bool? Playing, bool? Backlog, bool? Wishlist, DateOnly? StartedOn, DateOnly? FinishedOn)
{
    public static LogRequest OpenOnly(string id) => new(id, null, null, null, null, null, null);
    public static LogRequest Start(string id, DateOnly startedOn) => new(id, null, true, null, null, startedOn, null);
    public static LogRequest AddFinished(string id, DateOnly startedOn, DateOnly today) =>
        new(id, PlayedStatus.Completed, false, null, null, startedOn, today);

    public bool IsStartShape =>
        Playing == true && StartedOn is not null && Status is null && Backlog is null && Wishlist is null && FinishedOn is null;
}

public enum PromptAnswer { Yes, NotNow, Never, Skip, AddPlaying, AddFinished, Unseen, OpenLog }

public enum Shelf { Playing, Backlog, Wishlist }

public sealed class MarkAsChoice
{
    readonly bool[] _on = { true, false, false };
    readonly bool[] _touched = new bool[3];

    // Playing was turned off by picking a status (not by the user's own toggle): it counts as a
    // change only while a status stays picked, and comes back on when the status is unpicked.
    bool _playingOffByStatus;

    public PlayedStatus? Status { get; private set; }

    public bool IsOn(Shelf s) => _on[(int)s];

    public bool? Sent(Shelf s)
    {
        if (_touched[(int)s]) return _on[(int)s];
        if (s == Shelf.Playing && _playingOffByStatus && Status is not null) return false;
        return null;
    }

    public bool Changed => Status is not null || !_on[0] || _on[1] || _on[2];

    public void Pick(PlayedStatus s)
    {
        if (Status == s)
        {
            Status = null;
            if (_playingOffByStatus && !_touched[0]) _on[0] = true;
            _playingOffByStatus = false;
            return;
        }
        Status = s;
        if (_on[0])
        {
            _on[0] = false;
            // A Playing the user set themselves stays theirs (now off); otherwise the status did it.
            if (!_touched[0]) _playingOffByStatus = true;
        }
    }

    public void Toggle(Shelf s)
    {
        _on[(int)s] = !_on[(int)s];
        _touched[(int)s] = true;
    }
}

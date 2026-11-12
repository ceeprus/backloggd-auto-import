using System.Text.RegularExpressions;

namespace BackloggdLogger.Core;

// What the user picked under Mark as on the close popup: a played status and the shelf toggles they
// touched (null: not touched).
public sealed record StatusPick(PlayedStatus? Status, bool? Playing, bool? Backlog, bool? Wishlist)
{
    public bool Empty => Status is null && Playing is null && Backlog is null && Wishlist is null;

    // The status as Backloggd names it in input#status and log[status].
    public string? StatusValue => Status?.ToString().ToLowerInvariant();

    // "Shelved, Backlog on" in the order the popup shows them.
    public string Describe()
    {
        var parts = new List<string>();
        if (Status is { } s) parts.Add(s.ToString());
        if (Playing is { } p) parts.Add($"Playing {(p ? "on" : "off")}");
        if (Backlog is { } b) parts.Add($"Backlog {(b ? "on" : "off")}");
        if (Wishlist is { } w) parts.Add($"Wishlist {(w ? "on" : "off")}");
        return string.Join(", ", parts);
    }

    // The body of the notice after a save: "Saved as Shelved." or "Saved: Shelved, Backlog on."
    public string SavedBody() =>
        Status is { } s && Playing is null && Backlog is null && Wishlist is null ? $"Saved as {s}." : $"Saved: {Describe()}.";

    public string FailedTitle(string game) => $"Couldn't save {Describe()} for {game}";

    public string ExpiredTitle(string game) => $"Didn't save {Describe()} for {game}";
}

public sealed record StatusJob(string GameId, string Slug, StatusPick Pick);

// What became of a Mark as choice when the popup went away.
public enum PickKept { Nothing, Queued, NoMatch }

// After a status save run: tell the user it was saved, or that the app gave up on it; Expired: it
// waited more than a day and wasn't saved.
public enum StatusNotice { None, Saved, GaveUp, Expired }

// Mark as choices wait here to be saved, one per game; a newer one replaces an older one not yet sent. A pass
// tries each once; three failures give up and the user is told.
public sealed class StatusSaves(AppState state, Action save, Func<DateTime>? clock = null)
{
    // How long a choice may wait to be saved.
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    DateTime Now => (clock ?? (() => DateTime.Now))();

    readonly HashSet<string> _tried = new(StringComparer.Ordinal);
    int _stops;

    public bool Due { get; private set; }

    public void Kick()
    {
        Due = true;
        _tried.Clear();
        _stops = 0;
    }

    public PickKept Keep(string gameId, StatusPick pick)
    {
        if (pick.Empty || !state.Games.TryGetValue(gameId, out var g)) return PickKept.Nothing;
        g.PendingPick = pick;
        g.PickTries = 0;
        g.PickGaveUp = false;
        g.PendingPickAt = Now;
        save();
        Kick();
        return Journal.IsSlug(g.Slug) ? PickKept.Queued : PickKept.NoMatch;
    }

    // Choices older than a day aren't saved: the app gives up on them (Open log still has them).
    // Returns the games to tell, once each.
    public IReadOnlyList<GameRecord> Expire()
    {
        var now = Now;
        var expired = state.Games.Values
            .Where(g => g.PendingPick is not null && !g.PickGaveUp && now - g.PendingPickAt > MaxAge)
            .OrderBy(g => g.Id, StringComparer.Ordinal).ToList();
        foreach (var g in expired) g.PickGaveUp = true;
        if (expired.Count > 0) save();
        return expired;
    }

    public StatusJob? Next(JournalGate gate)
    {
        if (!Due || gate.Blocked) return null;
        // A game set to Never asks for nothing, saves nothing.
        var never = state.Games.Values.Where(g => g.Status == GameStatus.Never && g.PendingPick is not null).ToList();
        foreach (var n in never) n.DropPick();
        if (never.Count > 0) save();
        var g = state.Games.Values
            .Where(g => g.PendingPick is not null && !g.PickGaveUp && Journal.IsSlug(g.Slug) && !_tried.Contains(g.Id))
            .OrderBy(g => g.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (g is null)
        {
            Due = false;
            return null;
        }
        _tried.Add(g.Id);
        return new StatusJob(g.Id, g.Slug!, g.PendingPick!);
    }

    public StatusNotice Finish(StatusJob job, JournalOutcome outcome)
    {
        if (!state.Games.TryGetValue(job.GameId, out var g)) return StatusNotice.None;
        // A newer choice came in during the run: it is tried next, and this run's end doesn't count against it.
        var replaced = g.PendingPick != job.Pick;
        var notice = StatusNotice.None;
        switch (outcome.Kind)
        {
            case JournalOutcomeKind.Saved:
                Actions.OnSavedState(g, outcome.SavedStatus, outcome.SavedPlaying == true);
                if (!replaced)
                {
                    g.PendingPick = null;
                    g.PickTries = 0;
                }
                notice = StatusNotice.Saved;
                break;
            case JournalOutcomeKind.Unsupported when !replaced:
                g.PickGaveUp = true;
                notice = StatusNotice.GaveUp;
                break;
            case JournalOutcomeKind.Failed when !replaced:
                if (++g.PickTries >= Journal.MaxTries)
                {
                    g.PickGaveUp = true;
                    notice = StatusNotice.GaveUp;
                }
                break;
            case JournalOutcomeKind.Stopped:
                if (++_stops < Journal.MaxStopsPerPass) _tried.Remove(g.Id);
                break;
        }
        if (replaced)
        {
            Due = true;
            _tried.Remove(g.Id);
        }
        save();
        return notice;
    }

    // The user takes the choice into the log box themselves (Open log on the give-up notice).
    public StatusPick? TakeOver(string gameId)
    {
        if (!state.Games.TryGetValue(gameId, out var g) || g.PendingPick is not { } pick) return null;
        g.DropPick();
        save();
        return pick;
    }
}

// The owner's game-log-editor-mode cookie as it was before a run switched it: none (Existed false),
// or its value and attributes, to put back exactly.
public sealed record EditorCookieSaved(bool Existed, string? Value = null, string? Domain = null, string? Path = null,
    DateTime? Expires = null, string? SameSite = null, bool Secure = false, bool HttpOnly = false);

// The one Mark as save a run may send. NewLog: the game has no log and the save creates it; LoadedStatus: what
// #status showed when the box loaded (a new log keeps it when no status was picked).
public sealed record StatusExpect(string GameId, StatusPick Pick, JournalLogData? Data, string? CoverId = null, bool NewLog = false, string? LoadedStatus = null);

public static partial class JournalRequest
{
    [GeneratedRegex(@"^dates\[")]
    private static partial Regex AnyDateKey();

    // A Mark as save: no journal dates, every value as loaded except log[status] and the picked toggles, and
    // log[is_play], which may only become true when a status was picked. Returns why it must not go out.
    public static string? CheckStatusSave(List<KeyValuePair<string, string>>? fields, StatusExpect x)
    {
        if (fields is null) return "request body unreadable";
        if (x.Data is not { } data) return "log data unreadable";
        if (x.NewLog || data.NoLog) return x.NewLog && data.NoLog ? CheckNewLogSave(fields, x) : "new log or not: the box and the log data disagree";
        if (fields.GroupBy(f => f.Key).FirstOrDefault(g => g.Count() > 1) is { } twice) return $"request {Short(twice.Key)} twice";
        foreach (var f in fields)
        {
            if (DeletedKeys.Contains(f.Key))
            {
                if (f.Value.Trim().Length > 0) return $"request {f.Key}: removes";
                continue;
            }
            if (AnyDateKey().IsMatch(f.Key)) return $"request {Short(f.Key)}: journal dates in a status save";
            if (f.Key is "game_id" or "modal_type") continue;
            if (PlaythroughKey().Match(f.Key) is { Success: true } pk)
            {
                if (!PlaythroughKeys.Contains(pk.Groups[2].Value)) return $"unknown playthrough key {Short(f.Key)}";
                continue;
            }
            if (LogKey().Match(f.Key) is { Success: true } lk)
            {
                if (!SaveLogKeys.Contains(lk.Groups[1].Value)) return $"unknown log key {Short(f.Key)}";
                continue;
            }
            return Removal().IsMatch(f.Key) ? $"request {Short(f.Key)}: removes" : $"unknown key {Short(f.Key)}";
        }
        if (One(fields, "game_id") != x.GameId) return "request game_id";
        if (One(fields, "modal_type") is { } modal && modal != "quick") return $"request modal_type {Short(modal)}";

        foreach (var group in fields.Select(f => (Field: f, M: PlaythroughKey().Match(f.Key))).Where(e => e.M.Success).GroupBy(e => e.M.Groups[1].Value))
        {
            var sent = group.ToDictionary(e => e.M.Groups[2].Value, e => e.Field.Value);
            if (PlaythroughKeys.FirstOrDefault(k => !sent.ContainsKey(k)) is { } gone) return $"request playthroughs[{group.Key}][{gone}] missing";
            if (data.Playthroughs.FirstOrDefault(p => p.TryGetValue("id", out var id) && id == sent["id"].Trim()) is not { } had)
                return $"request playthroughs[{group.Key}]: playthrough {Short(sent["id"])} not in the log data";
            if (Totals(sent, had, "hours_played", "mins_played", 0) is { } bad) return $"request playthroughs[{group.Key}]: {bad}";
            foreach (var (k, v) in sent)
            {
                if (k is "hours_played" or "mins_played") continue;
                if (!had.TryGetValue(k, out var loaded)) return $"request playthroughs[{group.Key}][{k}]: not in the log data";
                if (!SameData(v, loaded)) return $"request playthroughs[{group.Key}][{k}]: differs from the log data";
            }
        }

        var log = fields.Select(f => (Field: f, M: LogKey().Match(f.Key))).Where(e => e.M.Success).ToDictionary(e => e.M.Groups[1].Value, e => e.Field.Value);
        if (SaveLogKeys.FirstOrDefault(k => !log.ContainsKey(k)) is { } missing) return $"request log[{missing}] missing";
        if (Totals(log, data.Log, "total_hours", "total_minutes", 0) is { } badTotal) return $"request log: {badTotal}";
        var picked = new Dictionary<string, bool?> { ["is_playing"] = x.Pick.Playing, ["is_backlog"] = x.Pick.Backlog, ["is_wishlist"] = x.Pick.Wishlist };
        foreach (var (k, v) in log)
        {
            if (k is "total_hours" or "total_minutes") continue;
            if (!data.Log.TryGetValue(k, out var loaded)) return $"request log[{k}]: not in the log data";
            if (k == "status" && x.Pick.StatusValue is { } status)
            {
                if (v.Trim() != status) return $"request log[status]: {Short(v)} sent, {status} picked";
                continue;
            }
            if (picked.TryGetValue(k, out var want) && want is { } on)
            {
                if (v.Trim() != (on ? "true" : "false")) return $"request log[{k}]: {Short(v)} sent, {(on ? "on" : "off")} picked";
                continue;
            }
            if (k == "is_play" && x.Pick.Status is not null && v.Trim() == "true") continue;
            if (k == "override_cover_id" && x.CoverId is { } cover && SameData(v, cover)) continue;
            if (!SameData(v, loaded)) return $"request log[{k}]: {Short(v)} sent, {Short(loaded)} loaded";
        }
        return null;
    }

    // A "Create Log" save, held to the body captured live on 2026-11-11: exactly these keys, an empty new
    // playthrough, and a log with nothing but the picks and the box's own defaults.
    static readonly string[] NewPlaythroughKeys =
    [
        "id", "title", "rating", "sync_sessions", "review", "review_spoilers", "platform", "hours_played", "mins_played", "is_master",
        "is_replay", "start_date", "finish_date", "edition_id", "edition_type", "medium_id", "played_platform", "storefront_id",
    ];

    static readonly string[] NewLogKeys = ["game_liked", "id", "last_edited_at", "override_cover_id", "is_play", "is_playing", "is_backlog", "is_wishlist", "status"];

    static readonly Dictionary<string, string[]> NewPlaythroughValues = new()
    {
        ["id"] = ["-1"], ["title"] = ["Log"], ["rating"] = ["NaN", "0"], ["sync_sessions"] = ["false"], ["review"] = [""],
        ["review_spoilers"] = ["false"], ["is_master"] = ["false"], ["is_replay"] = ["false"],
    };

    public static string? CheckNewLogSave(List<KeyValuePair<string, string>> fields, StatusExpect x)
    {
        var allowed = new HashSet<string>(["game_id", "modal_type", .. NewPlaythroughKeys.Select(k => $"playthroughs[0][{k}]"), .. NewLogKeys.Select(k => $"log[{k}]")]);
        if (fields.GroupBy(f => f.Key).FirstOrDefault(g => g.Count() > 1) is { } twice) return $"request {Short(twice.Key)} twice";
        if (fields.FirstOrDefault(f => !allowed.Contains(f.Key)) is { Key: not null } extra) return $"new log: unknown key {Short(extra.Key)}";
        if (allowed.FirstOrDefault(k => fields.All(f => f.Key != k)) is { } missing) return $"new log: {missing} missing";
        string V(string k) => fields.First(f => f.Key == k).Value;
        if (V("game_id") != x.GameId) return "request game_id";
        if (V("modal_type") != "quick") return $"request modal_type {Short(V("modal_type"))}";
        foreach (var k in NewPlaythroughKeys)
        {
            var sent = V($"playthroughs[0][{k}]");
            var want = NewPlaythroughValues.TryGetValue(k, out var w) ? w : [""];
            if (!want.Contains(sent)) return $"new log: playthroughs[0][{k}] \"{Short(sent)}\", expected {string.Join(" or ", want.Select(v => $"\"{v}\""))}";
        }
        var status = x.Pick.StatusValue ?? x.LoadedStatus;
        if (string.IsNullOrEmpty(status)) return "new log: no status";
        var want2 = new Dictionary<string, string>
        {
            ["id"] = "", ["last_edited_at"] = "", ["game_liked"] = "false", ["override_cover_id"] = x.CoverId ?? "",
            ["status"] = status, ["is_play"] = x.Pick.Status is null ? "false" : "true",
            ["is_playing"] = x.Pick.Playing == true ? "true" : "false",
            ["is_backlog"] = x.Pick.Backlog == true ? "true" : "false",
            ["is_wishlist"] = x.Pick.Wishlist == true ? "true" : "false",
        };
        foreach (var (k, v) in want2)
            if (V($"log[{k}]").Trim() != v) return $"new log: log[{k}] \"{Short(V($"log[{k}]"))}\", expected \"{v}\"";
        return null;
    }
}

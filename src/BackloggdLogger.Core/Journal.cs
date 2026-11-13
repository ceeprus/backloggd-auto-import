using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BackloggdLogger.Core;

public static partial class Journal
{
    public const int MaxTries = 3;

    // Raised when a build fixes what made journal runs fail; stored days get their tries back once.
    // 2: quick editor forced, looser log fit, tolerant totals (2026-11-11).
    public const int FormatVersion = 2;

    // Runs a pass may lose to a game starting or the screen getting busy before the day waits for
    // the next pass, so a flapping gate can't open the browser view over and over.
    public const int MaxStopsPerPass = 3;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SlugPattern();

    public static bool IsSlug(string? s) => s is not null && SlugPattern().IsMatch(s);

    // Only a game the user has logged on Backloggd: a game with no log would get one created by the
    // save, with the page's default status.
    public static bool Eligible(GameRecord g) =>
        !g.IsEmulator && IsSlug(g.Slug) && g.Status is GameStatus.Playing or GameStatus.Finished or GameStatus.Dropped;

    // The body of the notice for the days of one game that the app could not add. Unconfirmed: a
    // save was sent for one of them and Backloggd never answered, so the time may be there already.
    public static string NoticeBody(IReadOnlyList<(DateOnly Date, int Minutes, bool Unconfirmed)> days, DateTime today)
    {
        var sorted = days.OrderBy(d => d.Date).ToList();
        var total = sorted.Sum(d => d.Minutes);
        if (sorted.Any(d => d.Unconfirmed))
            return $"Check the journal for {DayList(sorted.Select(d => d.Date).ToList(), today)}: {Format.Minutes(total)} may already be there.";
        if (sorted.Count <= 3)
            return $"Add {Join(sorted.Select(d => $"{Format.Minutes(d.Minutes)} for {Format.Day(d.Date, today)}").ToList())} in the log.";
        return $"Add {Format.Minutes(total)} over {sorted.Count} days, {Format.Day(sorted[0].Date, today)} to {Format.Day(sorted[^1].Date, today)}, in the log.";
    }

    static string DayList(List<DateOnly> days, DateTime today) =>
        days.Count <= 3
            ? Join(days.Select(d => Format.Day(d, today)).ToList())
            : $"{days.Count} days, {Format.Day(days[0], today)} to {Format.Day(days[^1], today)}";

    static string Join(List<string> parts) =>
        parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
}

// What keeps a run from starting: any game running (tracked or not), a busy screen, an open Backloggd window,
// or one of the app's own windows in front (the hidden browser view could pull focus from it).
public readonly record struct JournalGate(bool GameRunning, bool ScreenBusy, bool WindowOpen, bool AppInFront)
{
    public bool Blocked => GameRunning || ScreenBusy || WindowOpen || AppInFront;
}

// Already/AlreadyMinutes: the total an unconfirmed save set and the minutes it added; AlreadySession: its session
// id ("new" for one the run made). Told: minutes the user was told to add by hand. Status: the app's status.
public sealed record JournalJob(string GameId, string Slug, DateOnly Date, int Add, int? Already, int AlreadyMinutes = 0, GameStatus Status = GameStatus.Playing, int Told = 0,
    string? AlreadySession = null)
{
    // A rerun after an unconfirmed save opened another session than the one that save was for.
    public bool TriedSessionChanged(string? sessionId) =>
        Already is not null && AlreadySession is { } tried && tried != "new" && tried != sessionId;

    // After an "add it yourself" notice, an existing session that holds at least the told minutes
    // has the told part already (the user typed it in): only play since the notice is added.
    public bool ToldThere(int sessionMinutes, bool newSession) => Told > 0 && !newSession && sessionMinutes >= Told;

    // The minutes a run adds to a session holding sessionMinutes: all pending, or what was played
    // since the notice when the told part is there.
    public int AddFor(int sessionMinutes, bool newSession) => ToldThere(sessionMinutes, newSession) ? Add - Told : Add;
}

public enum JournalOutcomeKind { Saved, Already, Unsupported, Failed, Stopped, Covered }

// Target: the session total the run set before it pressed Save (null when it never pressed it).
// Activated: the hidden window got focus during the run.
public sealed record JournalOutcome(JournalOutcomeKind Kind, string? Step = null, int? Target = null)
{
    public bool Activated { get; init; }

    // A confirmed Mark as save: the status and Playing toggle it sent (for the app's own record).
    public string? SavedStatus { get; init; }

    public bool? SavedPlaying { get; init; }

    public static JournalOutcome Saved(int target) => new(JournalOutcomeKind.Saved, null, target);
    public static readonly JournalOutcome Already = new(JournalOutcomeKind.Already);

    // The user added the time by hand after a notice, and nothing was played since: nothing saved,
    // the pending minutes count as handled.
    public static JournalOutcome Covered(int sessionMinutes, int told) =>
        new(JournalOutcomeKind.Covered, $"already there after a notice (session {sessionMinutes}m, told {told}m)");

    // A save after a notice whose told part was already in the session: the told minutes count as
    // the user's, the rest as sent by the app.
    public bool ToldThere { get; init; }
    public static JournalOutcome Unsupported(string step) => new(JournalOutcomeKind.Unsupported, step);
    public static JournalOutcome Failed(string step, int? target = null) => new(JournalOutcomeKind.Failed, step, target);

    // The run was stopped before it pressed Save (a game started): not the site's fault, so not a try.
    public static readonly JournalOutcome Stopped = new(JournalOutcomeKind.Stopped);
}

// One run's Save state. Once Save is about to be pressed, anything but a confirmed save ends as
// Failed(step, target), so the next run checks the session before adding again.
public sealed class JournalRun
{
    bool _messageTaken;

    public int? PressedTarget { get; private set; }

    // journal.js posts one message; any later one (another script on the page can post too) is
    // ignored, so it can never start a second save step.
    public bool TakeMessage()
    {
        if (_messageTaken) return false;
        _messageTaken = true;
        return true;
    }

    int? _committing;

    // The page is being asked to press Save. Until it answers, the run may end (a deadline, focus,
    // a browser crash), and then Save may have been pressed: the target is kept.
    public void Committing(int target) => _committing = target;

    public bool IsCommitting => _committing is not null;

    public void Answered(bool pressed)
    {
        if (pressed && _committing is { } t) PressedTarget = t;
        _committing = null;
    }

    // The run's end as the traffic saw it. A save that went out (sentTarget), or may have, keeps the target; a
    // refused save with nothing out drops it.
    public JournalOutcome Close(JournalOutcome outcome, JournalTraffic traffic, int? target)
    {
        var mayBeOut = traffic.SaveSent || traffic.MaybeSent;
        return Close(outcome, mayBeOut ? target : null, refused: traffic.Refused is not null && !mayBeOut);
    }

    public JournalOutcome Close(JournalOutcome outcome, int? sentTarget = null, bool refused = false)
    {
        if (sentTarget is { } sent) PressedTarget = sent;
        else if (refused) PressedTarget = null;
        else if (_committing is { } c) PressedTarget = c;
        if (PressedTarget is not { } t || outcome.Kind == JournalOutcomeKind.Saved) return outcome;
        var step = outcome.Step ?? outcome.Kind.ToString().ToLowerInvariant();
        return JournalOutcome.Failed(step, t) with { Activated = outcome.Activated };
    }
}

// Runs use the quick log editor, the one the checks are built for. The page picks it from its
// game-log-editor-mode cookie, so a run switches that to quick and puts the owner's value back afterwards.
public static class JournalEditorMode
{
    public const string Cookie = "game-log-editor-mode";

    public const string Quick = "quick";

    // existing: the cookie's value before the run, null when there was none. Switch: set quick for
    // the run. Restore: what to put back at the end (null: delete the cookie), or nothing to do.
    public static (bool Switch, bool Restore, string? RestoreValue) Plan(string? existing) =>
        existing == Quick ? (false, false, null) : (true, true, existing);
}

// The give-up notice for one game: every day of it the user has not been told about yet.
public sealed record JournalNotice(GameRecord Game, IReadOnlyList<PlayDay> Days);

// One pass sends every pending day once, oldest first; a failed day waits for the next pass, never a retry loop.
// saveChecked writes the state and says whether it reached the file; Prepare needs that before Save is pressed.
public sealed class JournalScheduler(AppState state, Action save, Func<bool>? saveChecked = null)
{
    readonly HashSet<(string, DateOnly)> _tried = new();
    readonly HashSet<(string, DateOnly)> _capped = new();
    int _stops;
    JournalJob? _inFlight;
    bool _kickWaiting;

    public bool Due { get; private set; }

    // A kick during a run waits for its Finish, and then only opens the pass again: the stop count
    // and the days that reached it stay, so a gate that keeps flapping can't restart them.
    public void Kick()
    {
        if (_inFlight is not null)
        {
            _kickWaiting = true;
            return;
        }
        Due = true;
        _tried.Clear();
        _capped.Clear();
        _stops = 0;
    }

    public JournalJob? Next(JournalGate gate)
    {
        if (!Due || !state.Settings.AutoAddJournalTime || gate.Blocked) return null;
        var next = state.Games.Values
            .Where(Journal.Eligible)
            .SelectMany(g => Offered(g).Select(d => (Game: g, Day: d)))
            .Where(x => !_tried.Contains((x.Game.Id, x.Day.Date)))
            .OrderBy(x => x.Day.Date).ThenBy(x => x.Game.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (next.Game is null)
        {
            Due = false;
            return null;
        }
        _tried.Add((next.Game.Id, next.Day.Date));
        _inFlight = new JournalJob(next.Game.Id, next.Game.Slug!, next.Day.Date, next.Day.PendingMinutes, next.Day.TriedTotal, next.Day.TriedMinutes, next.Game.Status, next.Day.ToldMinutes, next.Day.TriedSession);
        return _inFlight;
    }

    // A game's days a pass may offer. While one has an unconfirmed save, only that day runs: a session covering several
    // days would get another day's time on top, and the unconfirmed day could no longer tell whether its save landed.
    static IEnumerable<PlayDay> Offered(GameRecord g)
    {
        var pending = g.PendingJournal;
        var unconfirmed = pending.FirstOrDefault(d => d.TriedTotal is not null);
        return unconfirmed is null ? pending : [unconfirmed];
    }

    // Right before Save: the total it will set is written down first, so a save whose answer is lost is checked next
    // time instead of added twice. sessionId: the session the save is for (a page-made id is kept as "new").
    public bool Prepare(JournalJob job, int target, string? sessionId = null)
    {
        if (Day(job) is not { } day) return false;
        day.TriedTotal = target;
        day.TriedMinutes = job.Add;
        day.TriedSession = sessionId is null ? null : sessionId.StartsWith('-') ? "new" : sessionId;
        if (saveChecked is null)
        {
            save();
            return true;
        }
        try { return saveChecked(); }
        catch (Exception) { return false; }
    }

    // Applies a run's outcome to its day. True when this failure was the last try.
    public bool Finish(JournalJob job, JournalOutcome outcome)
    {
        try { return Apply(job, outcome); }
        finally
        {
            if (ReferenceEquals(job, _inFlight)) _inFlight = null;
            if (_kickWaiting && _inFlight is null)
            {
                _kickWaiting = false;
                Due = true;
                _tried.Clear();
                _tried.UnionWith(_capped);
            }
        }
    }

    bool Apply(JournalJob job, JournalOutcome outcome)
    {
        if (Day(job) is not { } day) return false;
        var wasGivenUp = day.GaveUp;
        switch (outcome.Kind)
        {
            case JournalOutcomeKind.Saved when outcome.ToldThere:
                Credit(day, Math.Min(job.Told, job.Add), byApp: false);
                Credit(day, job.Add - job.Told);
                break;
            case JournalOutcomeKind.Saved:
                Credit(day, job.Add);
                break;
            case JournalOutcomeKind.Already:
                // The earlier save had landed; play added since then goes in this same pass.
                Credit(day, day.TriedMinutes);
                _tried.Remove((job.GameId, job.Date));
                break;
            case JournalOutcomeKind.Covered:
                // Typed in by the user after a notice: counted as sent (not as sent by the app), and
                // play added since then goes in this same pass.
                Restore(day, job);
                Credit(day, job.Add, byApp: false);
                _tried.Remove((job.GameId, job.Date));
                break;
            case JournalOutcomeKind.Unsupported:
                Restore(day, job);
                if (day.UnsupportedOnce) day.JournalTries = Journal.MaxTries;
                else
                {
                    day.UnsupportedOnce = true;
                    day.JournalTries++;
                }
                break;
            case JournalOutcomeKind.Failed:
                day.JournalTries++;
                if (outcome.Target is { } t)
                {
                    day.TriedTotal = t;
                    day.TriedMinutes = job.Add;
                }
                else Restore(day, job);
                break;
            case JournalOutcomeKind.Stopped:
                Restore(day, job);
                if (++_stops < Journal.MaxStopsPerPass) _tried.Remove((job.GameId, job.Date));
                else _capped.Add((job.GameId, job.Date));
                save();
                return false;
        }
        save();
        return !wasGivenUp && day.GaveUp;
    }

    // Days given up on that the user has not been told about, one notice per game. Play added to
    // such a day later is told again, so it never goes missing without a word.
    public IReadOnlyList<JournalNotice> TakeNotices()
    {
        var notices = new List<JournalNotice>();
        foreach (var g in state.Games.Values.OrderBy(g => g.Id, StringComparer.Ordinal))
        {
            var days = g.Sessions.Where(d => d.GaveUp && d.PendingMinutes > d.ToldMinutes).ToList();
            if (days.Count == 0) continue;
            foreach (var d in days) d.ToldMinutes = d.PendingMinutes;
            notices.Add(new JournalNotice(g, days));
        }
        if (notices.Count > 0) save();
        return notices;
    }

    PlayDay? Day(JournalJob job) =>
        state.Games.TryGetValue(job.GameId, out var g) ? g.Sessions.FirstOrDefault(d => d.Date == job.Date) : null;

    // A run that never pressed Save leaves the day as it found it.
    static void Restore(PlayDay day, JournalJob job)
    {
        day.TriedTotal = job.Already;
        day.TriedMinutes = job.Already is null ? 0 : job.AlreadyMinutes;
        day.TriedSession = job.Already is null ? null : job.AlreadySession;
    }

    static void Credit(PlayDay day, int minutes, bool byApp = true)
    {
        minutes = Math.Clamp(minutes, 0, day.PendingMinutes);
        day.PushedMinutes += minutes;
        if (byApp) day.SentMinutes += minutes;
        day.JournalTries = 0;
        day.TriedTotal = null;
        day.TriedMinutes = 0;
        day.TriedSession = null;
        day.UnsupportedOnce = false;
        day.ToldMinutes = 0;
    }
}

public enum JournalScriptKind { Ready, Already, Unsupported, Failed, StatusReady, Covered }

// The one message journal.js posts when it ends. Ready: the session holds the new time and the log
// box waits for Save; Log holds the log's values as the page showed them when the box opened.
public sealed record JournalScriptResult(JournalScriptKind Kind, string Step, string? GameId = null, int? Target = null, string? Error = null)
{
    public int? Current { get; init; }

    // The session id the dialog showed (#play-date-id).
    public string? SessionId { get; init; }

    public IReadOnlyDictionary<string, string?> Log { get; init; } = new Dictionary<string, string?>();

    // The dialog's "start" or "finish" box was ticked.
    public bool SessionBoxes { get; init; }

    // A Mark as run: the log box's values after the picks were made (Log holds them before).
    public IReadOnlyDictionary<string, string?> After { get; init; } = new Dictionary<string, string?>();

    // A Mark as run on a game with no log: the box's log id and its one save button's label.
    public bool NewLog { get; init; }

    public string? LogId { get; init; }

    public string? SaveLabel { get; init; }

    // The log box's checked cover choice, sent as log[override_cover_id] (null: none checked).
    public string? CoverId { get; init; }

    // The script made the session itself (the day had none), under this playthrough (#current_playthrough).
    public bool NewSession { get; init; }

    public string? PlaythroughId { get; init; }

    // The script ticked the playthrough's "Sync sessions" box; it edits a session from an earlier day
    // that covers this one.
    public bool SyncTurnedOn { get; init; }

    public bool Continued { get; init; }

    // The session held the minutes a notice told the user to add: the script added only the rest.
    public bool ToldThere { get; init; }

    public static JournalScriptResult Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "type") != "journal") return Bad();
            var gameId = Str(root, "gameId");
            var target = Int(root, "target");
            if (True(root, "ok"))
            {
                var current = Int(root, "current");
                var sessionId = Str(root, "sessionId");
                if (True(root, "statusReady"))
                {
                    if (gameId is null || Values(root, "log") is not { } before || Values(root, "after") is not { } after) return Bad();
                    return new(JournalScriptKind.StatusReady, "ready", gameId)
                    {
                        Log = before, After = after, CoverId = Str(root, "coverId"), NewLog = True(root, "newLog"),
                        PlaythroughId = Str(root, "playthroughId"), LogId = Str(root, "logId"), SaveLabel = Str(root, "saveLabel"),
                    };
                }
                if (True(root, "already")) return new(JournalScriptKind.Already, "already", gameId) { Current = current, SessionId = sessionId, Continued = True(root, "continued") };
                if (True(root, "covered")) return new(JournalScriptKind.Covered, "covered", gameId) { Current = current, SessionId = sessionId, Continued = True(root, "continued") };
                if (True(root, "ready") && target is > 0 && current is >= 0 && gameId is not null
                    && root.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("sessionBoxes", out var boxes) && boxes.ValueKind == JsonValueKind.Array && boxes.GetArrayLength() == 2
                    && boxes.EnumerateArray().All(b => b.ValueKind is JsonValueKind.True or JsonValueKind.False))
                {
                    var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                    foreach (var p in log.EnumerateObject())
                        values[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                    return new(JournalScriptKind.Ready, "ready", gameId, target)
                    {
                        Current = current, Log = values, SessionId = sessionId, SessionBoxes = boxes.EnumerateArray().Any(b => b.ValueKind == JsonValueKind.True),
                        CoverId = Str(root, "coverId"),
                        NewSession = True(root, "newSession"),
                        PlaythroughId = Str(root, "playthroughId"),
                        SyncTurnedOn = True(root, "syncTurnedOn"),
                        Continued = True(root, "continued"),
                        ToldThere = True(root, "toldThere"),
                    };
                }
                return Bad();
            }
            var step = Str(root, "step") ?? "unknown";
            var error = Str(root, "error");
            return True(root, "unsupported")
                ? new(JournalScriptKind.Unsupported, step, gameId, null, error)
                : new(JournalScriptKind.Failed, step, gameId, null, error);
        }
        catch (JsonException)
        {
            return Bad();
        }
    }

    static JournalScriptResult Bad() => new(JournalScriptKind.Failed, "message");

    static Dictionary<string, string?>? Values(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var o) || o.ValueKind != JsonValueKind.Object) return null;
        var d = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var p in o.EnumerateObject()) d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
        return d;
    }

    static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool True(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

// What the second script (the one that presses Save) answered. Anything unreadable counts as
// pressed: the run can't tell, so it is treated as a save that may have gone out.
public sealed record JournalCommitResult(bool Pressed, string Step)
{
    // Runs the commit script with a time limit. A script that doesn't answer in time, or fails, may
    // have pressed Save: it counts as pressed.
    public static async Task<JournalCommitResult> RunAsync(Func<Task<string>> execute, TimeSpan limit)
    {
        Task<string> run;
        try { run = execute(); }
        catch (Exception) { return new(true, "commit"); }
        if (await Task.WhenAny(run, Task.Delay(limit)) != run)
        {
            _ = run.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return new(true, "commit timeout");
        }
        try { return Parse(await run); }
        catch (Exception) { return new(true, "commit"); }
    }

    public static JournalCommitResult Parse(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "");
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Null) return new(false, "commit missing");
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("pressed", out var p)) return new(true, "commit answer");
            if (p.ValueKind == JsonValueKind.True) return new(true, "save");
            if (p.ValueKind != JsonValueKind.False) return new(true, "commit answer");
            var step = root.TryGetProperty("step", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "commit";
            return new(false, step);
        }
        catch (JsonException)
        {
            return new(true, "commit answer");
        }
    }
}

// The request bodies the hidden window checks. JSON is flattened to the bracket keys a form would use
// (log[status], dates[1][0][hours]).
public static partial class JournalRequest
{
    // A short, log-safe view of a request body: printable ASCII kept, everything else as \xNN.
    public static string Printable(byte[] body, int max)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in body.Take(max)) sb.Append(b is >= 0x20 and < 0x7f ? ((char)b).ToString() : $"\\x{b:X2}");
        if (body.Length > max) sb.Append("...");
        return sb.ToString();
    }

    [GeneratedRegex(@"^dates\[([^\]]+)\]\[(\d+)\]\[([A-Za-z_]+)\]$")]
    private static partial Regex DateKey();



    // Only urlencoded bodies, as every request the page sends is: anything else could be read
    // differently here and by the site, so it is unreadable (null).
    public static List<KeyValuePair<string, string>>? ParseBody(string? contentType, byte[]? body)
    {
        if (body is null) return null;
        var type = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (type != "application/x-www-form-urlencoded") return null;
        try { return UrlEncoded(System.Text.Encoding.UTF8.GetString(body)); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return null; }
    }

    static List<KeyValuePair<string, string>> UrlEncoded(string text)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var k = i < 0 ? pair : pair[..i];
            var v = i < 0 ? "" : pair[(i + 1)..];
            list.Add(new(Unescape(k), Unescape(v)));
        }
        return list;
    }

    static string Unescape(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    // "2026-11-08", "2026-11-08T00:00:00..." or "Nov 8, 2026".
    public static DateOnly? ParseDate(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length >= 10 && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)
            && (s.Length == 10 || s[10] is 'T' or ' ')) return iso;
        return DateOnly.TryParseExact(s, ["MMM d, yyyy", "MMMM d, yyyy"], CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var d) ? d : null;
    }

    // A day as the dialog's request writes it, "Nov 8" with no year. The year is the anchor's, except across the new year:
    // a December day beside a January anchor is the year before, a January day beside a December one the year after.
    public static DateOnly? ParseDay(string? s, DateOnly anchor)
    {
        if (ParseDate(s) is { } full) return full;
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (!DateTime.TryParseExact(s, ["MMM d", "MMMM d"], CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var md)) return null;
        var year = anchor.Year;
        if (anchor.Month == 12 && md.Month == 1) year++;
        else if (anchor.Month == 1 && md.Month == 12) year--;
        return DateOnly.FromDateTime(new DateTime(year, 1, 1)).AddMonths(md.Month - 1).AddDays(md.Day - 1) is var d && d.Month == md.Month ? d : null;
    }

    // '' is 0 (a session with hours and no minutes); anything but a whole number is null.
    static int? Whole(string? s)
    {
        s = s?.Trim() ?? "";
        if (s.Length == 0) return 0;
        return s.All(char.IsAsciiDigit) && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    static string? One(List<KeyValuePair<string, string>> fields, string key)
    {
        var hits = fields.Where(f => f.Key == key).ToList();
        return hits.Count == 1 ? hits[0].Value : null;
    }

    // The two requests a run may send. Group 2 of the save is the game id.
    [GeneratedRegex(@"^https://(www\.)?backloggd\.com/api/user/\d+/log/(\d+)/?(\?.*)?$")]
    public static partial Regex SaveEndpoint();

    [GeneratedRegex(@"^https://(www\.)?backloggd\.com/open-date-modal/?(\?.*)?$")]
    public static partial Regex DialogEndpoint();

    [GeneratedRegex(@"^https://(www\.)?backloggd\.com/users/sign_in")]
    public static partial Regex SignIn();

    // The log box: its editor HTML (a POST with editor_type=quick) and its data (a GET). Group 2 is the game id.
    [GeneratedRegex(@"^https://(www\.)?backloggd\.com/render-editor/(\d+)/?(\?.*)?$")]
    public static partial Regex RenderEndpoint();

    [GeneratedRegex(@"^https://(www\.)?backloggd\.com/log/edit/(\d+)/?(\?.*)?$")]
    public static partial Regex LogDataEndpoint();

    [GeneratedRegex(@"destroy|delete", RegexOptions.IgnoreCase)]
    private static partial Regex Removal();

    // Sent with every save; empty in a normal one (an empty list is left out, null is sent as "").
    static readonly HashSet<string> DeletedKeys = ["deleted_dates", "deleted_playthroughs", "deleted_library_entries", "deleted_edition_time_entries"];




    // The fields of the edited date entry as read live on 2026-11-09; any other one is refused.
    static readonly HashSet<string> EntryKeys = ["id", "range_start_date", "range_end_date", "edited", "status", "note", "hours", "minutes", "start_date", "finish_date", "privacy"];

    // Entry fields that must come back as the dialog request had them (entry field, play_data field).
    static readonly (string Entry, string Dialog)[] SameAsDialog =
        [("note", "note"), ("status", "status"), ("privacy", "privacy"), ("start_date", "start_date"), ("finish_date", "finish_date")];

    // The /open-date-modal/ requests the run saw: exactly one, for the job's day, for a session whose time is the one the
    // dialog showed. Returns the failure step or the session. covering: it started before the day and runs over it.
    public static (string? Step, JournalDialog? Dialog) CheckDialog(IReadOnlyList<List<KeyValuePair<string, string>>?> requests, DateOnly date, int current, string? pageSessionId = null, bool covering = false)
    {
        if (requests.Count != 1) return ($"session request count {requests.Count}", null);
        if (requests[0] is not { } f) return ("session request body", null);
        var start = ParseDay(One(f, "play_data[start]"), date);
        if (start is null) return ("session request day unreadable", null);
        if (covering ? start >= date : start != date) return ($"session request day {start:yyyy-MM-dd}", null);
        if (One(f, "play_data[end]") is { } endText && (ParseDay(endText, date) is not { } end || end < start)) return ("session request end", null);
        if (covering && !(ParseDay(One(f, "play_data[end]"), date) >= date)) return ("session request doesn't reach the day", null);
        var id = One(f, "play_data[id]");
        if (string.IsNullOrWhiteSpace(id)) return ("session request id", null);
        if (pageSessionId is not null && pageSessionId.Trim() != id.Trim()) return ("session id on the page", null);
        var h = Whole(One(f, "play_data[hours]"));
        var m = Whole(One(f, "play_data[minutes]"));
        if (h is null || m is null || One(f, "play_data[hours]") is null) return ("session request time unreadable", null);
        if (h * 60 + m != current) return ($"session request time {h * 60 + m}m, dialog {current}m", null);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var g in f.Where(x => x.Key.StartsWith("play_data[", StringComparison.Ordinal) && x.Key.EndsWith(']')).GroupBy(x => x.Key[10..^1]))
            values[g.Key] = g.Count() == 1 ? g.First().Value : null;
        return (null, new JournalDialog(id.Trim(), values));
    }

    // A session the run makes for an empty day: a negative (page-made) id, under a playthrough of the log, on a day no
    // loaded session covers. That is Backloggd's own word the day is empty, so a save that already landed gets no twin.
    public static string? CheckNewSession(JournalLogData? data, DateOnly date, string? playthroughId, string? sessionId)
    {
        if (data is null) return "log data unreadable";
        if (sessionId is null || !NewSessionId().IsMatch(sessionId)) return $"new session id {Short(sessionId ?? "")}";
        if (playthroughId is null || !playthroughId.All(char.IsAsciiDigit) || playthroughId.Length == 0) return "new session playthrough unreadable";
        if (!data.Playthroughs.Any(p => p.TryGetValue("id", out var id) && id == playthroughId)) return $"new session playthrough {Short(playthroughId)} not in the log data";
        if (data.SessionDays.Any(d => d.Start is null)) return "log data session days unreadable";
        if (data.SessionDays.Any(d => d.Start <= date && date < (d.End is { } end && end > d.Start ? end : d.Start!.Value.AddDays(1))))
            return "the day already has a session in the log data";
        return null;
    }

    [GeneratedRegex(@"^-[1-9]\d*$")]
    private static partial Regex NewSessionId();

    // A session from an earlier day that the run adds to: Backloggd's log data must have it starting
    // before the day and covering it (its range_end_date is exclusive).
    public static string? CheckCovering(JournalLogData? data, DateOnly date, string sessionId)
    {
        if (data?.Session(sessionId) is not { } session) return $"session {Short(sessionId)} not in the log data";
        if (session.Start is not { } start) return "log data session days unreadable";
        var end = session.End is { } e && e > start ? e : start.AddDays(1);
        return start < date && date < end ? null : $"session {Short(sessionId)} doesn't cover the day in the log data";
    }

    // Sync sessions of the session's playthrough: off in the log data means the script must have
    // ticked it, and only the current playthrough's box (the one the log box shows) can be ticked.
    public static string? CheckSync(JournalLogData? data, string? sessionPlaythrough, string? currentPlaythrough, bool turnedOn)
    {
        if (data is null) return "log data unreadable";
        if (data.Playthroughs.FirstOrDefault(p => p.TryGetValue("id", out var id) && id == sessionPlaythrough) is not { } p)
            return $"sync: playthrough {Short(sessionPlaythrough ?? "")} not in the log data";
        var loadedOn = (p.GetValueOrDefault("sync_sessions") ?? "").Trim() == "true";
        if (loadedOn && turnedOn) return "sync box was off on the page but on in the log data";
        if (!loadedOn && !turnedOn) return "sync sessions off and not turned on";
        if (turnedOn && sessionPlaythrough != currentPlaythrough) return $"sync box is playthrough {Short(currentPlaythrough ?? "")}'s, session in {Short(sessionPlaythrough ?? "")}";
        return null;
    }

    [GeneratedRegex(@"^dates\[([^\]]+)\]\[(\d+)\]\[tags\]\[\]$")]
    private static partial Regex TagKey();

    // The game must be logged in the app (Playing, Finished or Dropped). Backloggd's status isn't mapped against it; the
    // box only has to show what the loaded log data says, so an empty status passes only when the data's is empty too.
    public static string? LogFits(GameStatus app, IReadOnlyDictionary<string, string?> log, string? loadedStatus)
    {
        if (app is not (GameStatus.Playing or GameStatus.Finished or GameStatus.Dropped)) return $"app status {app}";
        var status = log.TryGetValue("status", out var s) ? s?.Trim() ?? "" : "";
        if (status.Length == 0 && !string.IsNullOrWhiteSpace(loadedStatus)) return "log status empty, log data has one";
        return null;
    }

    // The keys a save carries, as captured from a real (blocked) save on 2026-11-09. Any other key,
    // and any of these twice, is refused by name.
    static readonly HashSet<string> PlaythroughKeys =
    [
        "id", "created_at", "updated_at", "rating", "review", "review_spoilers", "medium_id", "storefront_id", "platform", "played_platform",
        "sync_sessions", "hours_played", "mins_played", "hours_finished", "mins_finished", "hours_mastered", "mins_mastered", "is_master",
        "is_replay", "title", "start_date", "finish_date", "edition_id", "edition_type",
    ];

    static readonly HashSet<string> SaveLogKeys =
    [
        "id", "status", "rating", "is_play", "is_playing", "is_backlog", "is_wishlist", "game_liked", "time_source",
        "total_hours", "total_minutes", "avg_finish_time", "avg_master_time", "override_cover_id", "last_edited_at",
    ];

    [GeneratedRegex(@"^playthroughs\[(\d+)\]\[([A-Za-z_]+)\]$")]
    private static partial Regex PlaythroughKey();

    [GeneratedRegex(@"^log\[([A-Za-z_]+)\]$")]
    private static partial Regex LogKey();

    // The log save against what the run knows: the game id, exactly one edited journal date, every other value as loaded
    // except the played totals (up by exactly the minutes added), and no key outside the captured set. Null: it may go.
    public static string? CheckSave(List<KeyValuePair<string, string>>? fields, JournalExpect x)
    {
        if (fields is null) return "request body unreadable";
        if (x.Data is not { } data) return "log data unreadable";
        // A tag list repeats its key once per tag (dates[..][..][tags][]); any other key appears once.
        if (fields.Where(f => !TagKey().IsMatch(f.Key)).GroupBy(f => f.Key).FirstOrDefault(g => g.Count() > 1) is { } twice) return $"request {Short(twice.Key)} twice";

        // Every key is one of the captured ones; deleted_* only when empty.
        foreach (var f in fields)
        {
            if (DeletedKeys.Contains(f.Key))
            {
                if (f.Value.Trim().Length > 0) return $"request {f.Key}: removes";
                continue;
            }
            if (f.Key is "game_id" or "modal_type" || DateKey().IsMatch(f.Key) || TagKey().IsMatch(f.Key)) continue;
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

        var entries = fields.Select(f => (Field: f, M: DateKey().Match(f.Key))).Where(e => e.M.Success)
            .GroupBy(e => (e.M.Groups[1].Value, e.M.Groups[2].Value))
            .ToList();
        if (entries.Count != 1) return $"request dates: {entries.Count} entries";
        var playthroughId = entries[0].Key.Item1;
        var byKey = entries[0].GroupBy(e => e.M.Groups[3].Value).ToList();
        if (byKey.FirstOrDefault(g => !EntryKeys.Contains(g.Key)) is { } extra) return $"request dates: unknown field {Short(extra.Key)}";
        var entry = byKey.ToDictionary(g => g.Key, g => g.First().Field.Value);
        // The whole captured set: a field left out could be cleared by the site.
        if (EntryKeys.FirstOrDefault(k => !entry.ContainsKey(k)) is { } missing) return $"request dates: {missing} missing";
        // Tags go as a list (one dates[..][..][tags][] per tag, in order), only for this entry.
        var tagFields = fields.Select(f => (Field: f, M: TagKey().Match(f.Key))).Where(e => e.M.Success).ToList();
        if (tagFields.Any(e => (e.M.Groups[1].Value, e.M.Groups[2].Value) != entries[0].Key)) return "request dates: tags of another entry";
        var tagsSent = tagFields.Select(e => e.Field.Value).ToList();
        string? E(string k) => entry.TryGetValue(k, out var v) ? v : null;
        if (E("edited")?.Trim().ToLowerInvariant() is not ("true" or "1")) return "request dates: not edited";
        if (E("id")?.Trim() != x.Dialog.Id) return "request dates: session id";
        // A session from an earlier day keeps its own start; any other starts on the day.
        var startDay = x.Continued ? x.Data?.Session(x.Dialog.Id)?.Start : x.Date;
        if (startDay is null || ParseDate(E("range_start_date")) != startDay) return "request dates: day";
        var h = Whole(E("hours"));
        var m = Whole(E("minutes"));
        if (h is null || m is null || h * 60 + m != x.Target) return $"request dates: time {E("hours")}h {E("minutes")}m, expected {x.Target}m";
        foreach (var (k, d) in SameAsDialog)
        {
            if (E(k) is not { } sent) continue;
            if (!x.Dialog.Values.TryGetValue(d, out var had) || had is null) return $"request dates: {k} not in the dialog request";
            // The dialog's Save always sets the status to 5 (read from Backloggd's handler); it was
            // empty or 5 before. Any other status must stay.
            if (k == "status" && sent.Trim() == "5" && had.Trim() is "" or "5") continue;
            // Dates may be written two ways ("2026-11-08", "Nov 8, 2026", "Nov 8"); a mismatch names both.
            if (k is "start_date" or "finish_date" && ParseDay(sent, x.Date) is { } sd && ParseDay(had, x.Date) is { } hd && sd == hd) continue;
            if (!SameValue(sent, had)) return $"request dates: {k} \"{Short(sent)}\", dialog had \"{Short(had)}\"";
        }
        if (E("range_end_date") is { } endSent)
        {
            if (!x.Dialog.Values.TryGetValue("end", out var endHad) || ParseDay(endHad, x.Date) is not { } lastDay) return "request dates: range_end_date not in the dialog request";
            if (ParseDate(endSent) != lastDay.AddDays(1)) return $"request dates: range_end_date \"{Short(endSent)}\", dialog's last day \"{Short(endHad!)}\"";
        }
        if (!data.Playthroughs.Any(p => p.TryGetValue("id", out var id) && id == playthroughId)) return $"request dates: playthrough {Short(playthroughId)} not in the log data";
        if (x.NewSessionPlaythrough is { } newUnder)
        {
            // A new session: exactly as the page makes one (status 5, nothing but its day and time),
            // under the playthrough it was made in, on a day the log data has no session for.
            if (CheckNewSession(data, x.Date, newUnder, x.Dialog.Id) is { } why) return $"request dates: {why}";
            if (playthroughId != newUnder) return $"request dates: new session under playthrough {Short(playthroughId)}, made under {Short(newUnder)}";
            if (ParseDate(E("range_end_date")) != x.Date.AddDays(1)) return "request dates: new session range_end_date";
            if (E("status")?.Trim() != "5") return "request dates: new session status";
            foreach (var k in new[] { "note", "privacy", "start_date", "finish_date" })
                if ((E(k)?.Trim() ?? "") != "") return $"request dates: new session {k}";
            if (tagsSent.Count > 0) return "request dates: new session tags";
        }
        else
        {
            if (x.Dialog.Id.StartsWith('-')) return "request dates: session id negative";
            if (!(data.PlayDates.TryGetValue(playthroughId, out var held) && held.Contains(x.Dialog.Id)))
                return $"request dates: playthrough {Short(playthroughId)} doesn't hold session {Short(x.Dialog.Id)}";
            if (x.Continued && CheckCovering(data, x.Date, x.Dialog.Id) is { } notCovered) return $"request dates: {notCovered}";
        }
        // The session's tags as Backloggd has them, same items in the same order; none for a new one.
        var tagsHad = x.NewSessionPlaythrough is not null ? [] : data.Session(x.Dialog.Id)?.Tags;
        if (tagsHad is null) return "request dates: session tags unreadable in the log data";
        if (!tagsSent.SequenceEqual(tagsHad)) return $"request dates: tags [{Short(string.Join(",", tagsSent))}] sent, [{Short(string.Join(",", tagsHad))}] in the log data";

        // Playthroughs: every value as loaded; the session's playthrough's played time rises by the
        // minutes added when it adds up its sessions.
        var added = x.Target - x.Current;
        foreach (var group in fields.Select(f => (Field: f, M: PlaythroughKey().Match(f.Key))).Where(e => e.M.Success).GroupBy(e => e.M.Groups[1].Value))
        {
            var sent = group.ToDictionary(e => e.M.Groups[2].Value, e => e.Field.Value);
            if (PlaythroughKeys.FirstOrDefault(k => !sent.ContainsKey(k)) is { } gone) return $"request playthroughs[{group.Key}][{gone}] missing";
            var pid = sent["id"];
            if (data.Playthroughs.FirstOrDefault(p => p.TryGetValue("id", out var id) && id == pid.Trim()) is not { } had)
                return $"request playthroughs[{group.Key}]: playthrough {Short(pid)} not in the log data";
            var syncTurnedOn = x.SyncTurnedOn && pid.Trim() == playthroughId;
            if (syncTurnedOn)
            {
                // The script ticked Sync sessions: it goes from off to on, and the played time is the
                // loaded one or the sum of the playthrough's sessions with the minutes added.
                if ((had.GetValueOrDefault("sync_sessions") ?? "").Trim() != "false" || sent["sync_sessions"].Trim() != "true")
                    return $"request playthroughs[{group.Key}][sync_sessions]: {Short(sent["sync_sessions"])} sent, {had.GetValueOrDefault("sync_sessions")} loaded";
                if (SyncedTotals(sent, had, data.SessionSum(playthroughId), added) is { } bad) return $"request playthroughs[{group.Key}]: {bad}";
            }
            else
            {
                // Only the session's playthrough, and only when it adds up its sessions, may rise.
                var rises = pid.Trim() == playthroughId && (had.GetValueOrDefault("sync_sessions") ?? "").Trim() == "true";
                if (Totals(sent, had, "hours_played", "mins_played", rises ? added : 0) is { } bad) return $"request playthroughs[{group.Key}]: {bad}";
            }
            foreach (var (k, v) in sent)
            {
                if (k is "hours_played" or "mins_played" || (syncTurnedOn && k == "sync_sessions")) continue;
                if (!had.TryGetValue(k, out var loaded)) return $"request playthroughs[{group.Key}][{k}]: not in the log data";
                if (!SameData(v, loaded)) return $"request playthroughs[{group.Key}][{k}]: differs from the log data";
            }
        }

        // The log: every value as loaded; its totals rise by the minutes added when it takes its
        // time from the sessions (time_source 1).
        var log = fields.Select(f => (Field: f, M: LogKey().Match(f.Key))).Where(e => e.M.Success).ToDictionary(e => e.M.Groups[1].Value, e => e.Field.Value);
        if (log.Count > 0)
        {
            if (SaveLogKeys.FirstOrDefault(k => !log.ContainsKey(k)) is { } gone) return $"request log[{gone}] missing";
            // The log's total may rise only when it takes its time from the sessions (time_source 1).
            var logRises = (data.Log.GetValueOrDefault("time_source") ?? "").Trim() == "1";
            if (Totals(log, data.Log, "total_hours", "total_minutes", logRises ? added : 0) is { } bad) return $"request log: {bad}";
            foreach (var (k, v) in log)
            {
                if (k is "total_hours" or "total_minutes") continue;
                if (!data.Log.TryGetValue(k, out var loaded)) return $"request log[{k}]: not in the log data";
                // The cover is sent from the box's checked cover choice, which the log data may leave null.
                if (k == "override_cover_id" && x.CoverId is { } cover && SameData(v, cover)) continue;
                if (!SameData(v, loaded)) return $"request log[{k}]: differs from the log data";
            }
        }
        return null;
    }

    // A played total sent (hours and minutes, both present) against the loaded one, in minutes: the
    // same (the page didn't add the sessions up) or up by exactly the minutes added, nothing else.
    internal static string? Totals(Dictionary<string, string> sent, IReadOnlyDictionary<string, string> had, string hoursKey, string minutesKey, int added)
    {
        var sh = sent.GetValueOrDefault(hoursKey);
        var sm = sent.GetValueOrDefault(minutesKey);
        var hh = had.GetValueOrDefault(hoursKey);
        var hm = had.GetValueOrDefault(minutesKey);
        // Sent as loaded, whatever the format: unchanged.
        if (sh is not null && sm is not null && SameData(sh, hh) && SameData(sm, hm)) return null;
        var raw = $"{hoursKey} {sh}/{sm} sent, {hh}/{hm} loaded";
        var oldTotal = Amount(hh) * 60 + Amount(hm);
        var newTotal = Amount(sh) * 60 + Amount(sm);
        if (oldTotal is null || newTotal is null) return $"{raw}, unreadable";
        if (Math.Abs(newTotal.Value - oldTotal.Value) < 0.5m || Math.Abs(newTotal.Value - (oldTotal.Value + added)) < 0.5m) return null;
        return added == 0 ? $"{raw}, expected {oldTotal:0.#}m" : $"{raw}, expected {oldTotal:0.#}m or {oldTotal + added:0.#}m";
    }

    // Played time of a playthrough whose Sync sessions was just ticked: as loaded (the site works it
    // out), or the sum of its sessions in the log data plus the minutes added, within half a minute.
    static string? SyncedTotals(Dictionary<string, string> sent, IReadOnlyDictionary<string, string> had, decimal? sessions, int added)
    {
        var sh = sent.GetValueOrDefault("hours_played");
        var sm = sent.GetValueOrDefault("mins_played");
        var hh = had.GetValueOrDefault("hours_played");
        var hm = had.GetValueOrDefault("mins_played");
        if (sh is not null && sm is not null && SameData(sh, hh) && SameData(sm, hm)) return null;
        var raw = $"hours_played {sh}/{sm} sent, {hh}/{hm} loaded, sessions {(sessions is { } t ? $"{t:0.#}m" : "unreadable")}";
        var newTotal = Amount(sh) * 60 + Amount(sm);
        // As loaded in amount: a new log loads 0/0 and the page sends both fields empty.
        if (newTotal is { } sentTotal && Amount(hh) * 60 + Amount(hm) is { } loadedTotal && sentTotal == loadedTotal) return null;
        if (newTotal is null || sessions is null) return $"{raw}, unreadable";
        return Math.Abs(newTotal.Value - (sessions.Value + added)) < 0.5m ? null : $"{raw}, expected {sessions + added:0.#}m";
    }

    // A total's part: a non-negative decimal, empty (or JSON null) as 0; anything else null.
    internal static decimal? Amount(string? s)
    {
        s = s?.Trim() ?? "";
        if (s.Length == 0) return 0;
        return decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
    }

    // A sent value against the loaded JSON value: the same text (line ends aside), the same number,
    // the same yes/no, or empty for a JSON null.
    internal static bool SameData(string sent, string? loaded)
    {
        var a = sent.Replace("\r\n", "\n").Trim();
        var b = (loaded ?? "").Replace("\r\n", "\n").Trim();
        if (a == b) return true;
        if (decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var da) && decimal.TryParse(b, NumberStyles.Number, CultureInfo.InvariantCulture, out var db)) return da == db;
        return a is "true" or "false" && b is "true" or "false" && a == b;
    }

    // What Backloggd answered the checked save. Only a plain 2xx that isn't a page (a sign-in page
    // after a redirect) and carries no error counts as saved.
    public static string? CheckSaveAnswer(int status, string? contentType, string? body, bool signInSeen)
    {
        if (status is >= 300 and < 400) return $"save answer redirect {status}";
        if (status is < 200 or >= 300) return $"save status {status}";
        if (signInSeen) return "save answer: sign-in page";
        if (body is null) return "save answer unreadable";
        if ((contentType ?? "").Contains("html", StringComparison.OrdinalIgnoreCase) || body.TrimStart().StartsWith('<')) return "save answer: a web page";
        if ((contentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase) || body.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "error", "errors" })
                        if (root.TryGetProperty(name, out var e) && !Empty(e)) return "save answer: error";
                    foreach (var name in new[] { "success", "ok", "saved" })
                        if (root.TryGetProperty(name, out var ok) && ok.ValueKind == JsonValueKind.False) return "save answer: not saved";
                }
            }
            catch (JsonException) { return "save answer: broken JSON"; }
        }
        return null;
    }

    static bool Empty(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => true,
        JsonValueKind.String => e.GetString()!.Length == 0,
        JsonValueKind.Array => e.GetArrayLength() == 0,
        JsonValueKind.Object => !e.EnumerateObject().Any(),
        _ => false,
    };

    // Equal text, the same day in two spellings, or the same yes/no.
    static bool SameValue(string a, string b)
    {
        a = a.Trim();
        b = b.Trim();
        if (a == b) return true;
        if (ParseDate(a) is { } da && ParseDate(b) is { } db) return da == db;
        return Bool(a) is { } ba && Bool(b) is { } bb && ba == bb && a.Length > 0 && b.Length > 0;
    }

    static string Short(string s) => s.Length <= 40 ? s : s[..40];

    static bool? Bool(string s) => s.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "on" => true,
        "false" or "0" or "off" or "" => false,
        _ => null,
    };
}

// The session as the dialog's /open-date-modal/ request named it: its id and every play_data field.
public sealed record JournalDialog(string Id, IReadOnlyDictionary<string, string?> Values);

// The one save a run may send, set right before Save. Current: the session's minutes before; Data: the log data
// loaded before Save (null: no save goes out); NewSessionPlaythrough: set when the run made the session itself.
public sealed record JournalExpect(
    string GameId, DateOnly Date, int Current, int Target, JournalDialog Dialog, bool SessionBoxes, JournalLogData? Data,
    string? CoverId = null, string? NewSessionPlaythrough = null, bool SyncTurnedOn = false, bool Continued = false);

// One session of the loaded log data: its id, playthrough, days (range_end_date exclusive), time,
// and tags in order (null when they aren't plain values).
public sealed record JournalSession(string Id, string PlaythroughId, DateOnly? Start, DateOnly? End, string Hours, string Minutes, IReadOnlyList<string>? Tags);

// The log data the log box loads (GET /log/edit/<gameId>): the log's and each playthrough's values as text (JSON
// null is "", true/false stay words). PlayDates: each playthrough's session ids, by playthrough id.
public sealed record JournalLogData(
    IReadOnlyDictionary<string, string> Log, IReadOnlyList<IReadOnlyDictionary<string, string>> Playthroughs,
    IReadOnlyDictionary<string, IReadOnlySet<string>> PlayDates,
    IReadOnlyList<(DateOnly? Start, DateOnly? End)> SessionDays,
    IReadOnlyList<JournalSession> Sessions)
{
    public JournalSession? Session(string id) => Sessions.FirstOrDefault(s => s.Id == id);

    // The game has no log on Backloggd: {"playthroughs":{},"most_recent_playthrough_id":null}, no game_log.
    public bool NoLog { get; init; }

    // The playthrough's sessions added up in minutes; null when a time can't be read.
    public decimal? SessionSum(string playthroughId)
    {
        decimal sum = 0;
        foreach (var s in Sessions.Where(s => s.PlaythroughId == playthroughId))
        {
            if (JournalRequest.Amount(s.Hours) is not { } h || JournalRequest.Amount(s.Minutes) is not { } m) return null;
            sum += h * 60 + m;
        }
        return sum;
    }

    // The shape read live on 2026-11-09: the log under "game_log", the playthroughs an object keyed
    // by playthrough id, each with that id. Anything else is unreadable (null), and no save goes out.
    public static JournalLogData? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // No log yet (read live 2026-11-11): exactly an empty playthroughs object and a null
            // most recent playthrough, nothing else.
            if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("game_log", out _)
                && root.TryGetProperty("playthroughs", out var none) && none.ValueKind == JsonValueKind.Object && !none.EnumerateObject().Any()
                && root.TryGetProperty("most_recent_playthrough_id", out var recent) && recent.ValueKind == JsonValueKind.Null
                && root.EnumerateObject().Count() == 2)
                return new JournalLogData(new Dictionary<string, string>(), [], new Dictionary<string, IReadOnlySet<string>>(), [], []) { NoLog = true };
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("game_log", out var log) || log.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("playthroughs", out var byId) || byId.ValueKind != JsonValueKind.Object) return null;
            var playthroughs = new List<IReadOnlyDictionary<string, string>>();
            var playDates = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
            var sessionDays = new List<(DateOnly?, DateOnly?)>();
            var sessions = new List<JournalSession>();
            foreach (var p in byId.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) return null;
                var fields = Scalars(p.Value);
                if (!fields.TryGetValue("id", out var id) || id != p.Name) return null;
                playthroughs.Add(fields);
                var ids = new HashSet<string>(StringComparer.Ordinal);
                if (p.Value.TryGetProperty("play_dates", out var dates))
                {
                    if (dates.ValueKind != JsonValueKind.Array) return null;
                    foreach (var d in dates.EnumerateArray())
                    {
                        if (d.ValueKind != JsonValueKind.Object) return null;
                        if (d.TryGetProperty("id", out var did) && did.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                            ids.Add(did.ValueKind == JsonValueKind.String ? did.GetString()! : did.GetRawText());
                        // Its days, as range_start_date and the exclusive range_end_date; unreadable ones stay null.
                        var day = Scalars(d);
                        var start = JournalRequest.ParseDate(day.GetValueOrDefault("range_start_date"));
                        var end = JournalRequest.ParseDate(day.GetValueOrDefault("range_end_date"));
                        sessionDays.Add((start, end));
                        sessions.Add(new JournalSession(day.GetValueOrDefault("id") ?? "", id, start, end,
                            day.GetValueOrDefault("hours") ?? "", day.GetValueOrDefault("minutes") ?? "", Tags(d)));
                    }
                }
                playDates[id] = ids;
            }
            return new JournalLogData(Scalars(log), playthroughs, playDates, sessionDays, sessions);
        }
        catch (JsonException) { return null; }
    }

    // A session's tags: plain values in order, none when absent or null; null when any isn't plain.
    static IReadOnlyList<string>? Tags(JsonElement session)
    {
        if (!session.TryGetProperty("tags", out var tags) || tags.ValueKind == JsonValueKind.Null) return [];
        if (tags.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var t in tags.EnumerateArray())
        {
            if (t.ValueKind == JsonValueKind.String) list.Add(t.GetString()!);
            else if (t.ValueKind == JsonValueKind.Number) list.Add(t.GetRawText());
            else return null;
        }
        return list;
    }

    static Dictionary<string, string> Scalars(JsonElement e)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
        {
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.String: d[p.Name] = p.Value.GetString()!; break;
                case JsonValueKind.Null: d[p.Name] = ""; break;
                case JsonValueKind.True: d[p.Name] = "true"; break;
                case JsonValueKind.False: d[p.Name] = "false"; break;
                case JsonValueKind.Number: d[p.Name] = p.Value.GetRawText(); break;
            }
        }
        return d;
    }
}

// Every request the hidden page makes, from any host and source, passes here. Reads go through; the only writes
// let out are the log box's editor request, the dialog's request, and the one save that fits Expect.
public sealed class JournalTraffic
{
    public List<List<KeyValuePair<string, string>>?> DialogRequests { get; } = new();

    // The game ids of the log box's editor requests (POST /render-editor/) and data reads (GET /log/edit/).
    public List<string> RenderedGameIds { get; } = new();

    public List<string> LogDataGameIds { get; } = new();

    // The log data the last GET /log/edit/ answered with, as the host read it (null: unreadable).
    public JournalLogData? LogData { get; set; }

    public bool LogDataRead { get; set; }

    public JournalExpect? Expect { get; private set; }

    public bool SaveSent { get; private set; }

    // The first save that was refused: the run ends with it, and no later write goes out.
    public string? Refused { get; private set; }

    // The run is over: nothing more goes out.
    public bool Closed { get; set; }

    // Refusing a request failed, so it may have gone out: the run keeps its total.
    public bool MaybeSent { get; set; }

    // The page went to the sign-in page after Save was pressed.
    public bool SignInSeen { get; private set; }

    public List<string> Notes { get; } = new();

    public void Arm(JournalExpect expect) => Expect = expect;

    // A Mark as save instead of a journal one: the same filter, with its own check.
    public StatusExpect? StatusExpect { get; private set; }

    public void ArmStatus(StatusExpect expect) => StatusExpect = expect;

    public bool Armed => Expect is not null || StatusExpect is not null;

    public string? ArmedGameId => Expect?.GameId ?? StatusExpect?.GameId;

    // The headers that turn a POST into another method on the site (Rack's method override).
    public static readonly string[] OverrideHeaders = ["X-HTTP-Method-Override", "X-HTTP-Method", "X-Method-Override"];

    // read gives the Content-Type and body, called only when needed; hasHeader says whether a header is present.
    // Returns whether the request may go out, and why not.
    public (bool Allow, string? Reason) Request(string method, string uri, Func<(string? ContentType, byte[]? Body)> read, Func<string, bool>? hasHeader = null)
    {
        var verb = (method ?? "").Trim().ToUpperInvariant();
        var isSave = JournalRequest.SaveEndpoint().IsMatch(uri);
        if (hasHeader is not null && OverrideHeaders.FirstOrDefault(hasHeader) is { } header)
            return Refuse($"method override header {header}", save: isSave);
        if (verb is "GET" or "HEAD" or "OPTIONS")
        {
            if (Armed && JournalRequest.SignIn().IsMatch(uri)) SignInSeen = true;
            if (verb == "GET" && JournalRequest.LogDataEndpoint().Match(uri) is { Success: true } data) LogDataGameIds.Add(data.Groups[2].Value);
            return (true, null);
        }
        if (Closed) return Refuse("after the run", save: false);
        if (Refused is not null) return Refuse("after a refused save", save: false);
        // The site reads a query as part of the request: the two before Save and the save carry none.
        var hasQuery = uri.Contains('?');
        if (verb == "POST" && JournalRequest.RenderEndpoint().Match(uri) is { Success: true } render)
        {
            if (hasQuery) return Refuse("log box request with a query", save: false);
            // The log box's editor: before Save only, one game, and nothing in the body but the editor type.
            if (Armed) return Refuse("log box request after Save", save: false);
            var game = render.Groups[2].Value;
            if (RenderedGameIds.Count > 0 && RenderedGameIds[0] != game) return Refuse($"log box for game {game}", save: false);
            var (type, body) = read();
            if (!(type ?? "").StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
                || body is null || System.Text.Encoding.UTF8.GetString(body) != "editor_type=quick")
                return Refuse($"log box request body (type {(type is null ? "none" : type)}, {(body is null ? "no body" : body.Length + " bytes: " + JournalRequest.Printable(body, 64))})", save: false);
            RenderedGameIds.Add(game);
            return (true, null);
        }
        if (verb == "POST" && JournalRequest.DialogEndpoint().IsMatch(uri))
        {
            if (hasQuery) return Refuse("session dialog request with a query", save: false);
            if (Armed) return Refuse("session dialog request after Save", save: false);
            var (type, body) = read();
            DialogRequests.Add(JournalRequest.ParseBody(type, body));
            return (true, null);
        }
        var save = JournalRequest.SaveEndpoint().Match(uri);
        if (!save.Success)
            return Refuse($"write to {(Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : "?")}", save: false);
        if (ArmedGameId is not { } armedGame) return Refuse("save before Save was pressed", save: true);
        if (SaveSent) return Refuse("a second save", save: true);
        if (verb is not ("POST" or "PUT" or "PATCH")) return Refuse($"save sent as {verb}", save: true);
        if (save.Groups[2].Value != armedGame) return Refuse($"save for game {save.Groups[2].Value}", save: true);
        if (hasQuery) return Refuse("save with a query", save: true);
        var (ct, bytes) = read();
        var fields = JournalRequest.ParseBody(ct, bytes);
        var check = Expect is { } x ? JournalRequest.CheckSave(fields, x) : JournalRequest.CheckStatusSave(fields, StatusExpect!);
        if (check is { } why)
        {
            // The key names only, never the values: the first supervised run shows what the save carries.
            if (fields is not null) Notes.Add("save keys: " + string.Join(", ", fields.Select(f => f.Key)));
            return Refuse(why, save: true);
        }
        SaveSent = true;
        Notes.Add($"save {verb} checked and sent");
        return (true, null);
    }

    // Before the save step: the log box was rendered and loaded for this game, and only for it.
    public string? CheckLogBox(string gameId)
    {
        if (RenderedGameIds.Count == 0) return "log box request not seen";
        if (RenderedGameIds.Any(g => g != gameId)) return $"log box for game {RenderedGameIds.First(g => g != gameId)}";
        if (LogDataGameIds.Count == 0) return "log data not seen";
        if (LogDataGameIds.Any(g => g != gameId)) return $"log data for game {LogDataGameIds.First(g => g != gameId)}";
        if (!LogDataRead) return "log data not read";
        if (LogData is null) return "log data unreadable";
        return null;
    }

    (bool, string?) Refuse(string reason, bool save)
    {
        if (save) Refused ??= reason;
        Notes.Add("refused: " + reason);
        return (false, reason);
    }
}

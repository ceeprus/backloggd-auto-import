using System.Globalization;

namespace BackloggdLogger.Core;

public enum StartPrompt { None, AskAdd }
public enum ClosePrompt { None, AskAddAfterClose, AskStatus }

public static class PromptPolicy
{
    public static StartPrompt OnStart(GameRecord g, bool paused, bool askOnStart = true) =>
        askOnStart && !paused && g.Status == GameStatus.Unknown && g.PendingStart is null && !g.Asked ? StartPrompt.AskAdd : StartPrompt.None;

    public static ClosePrompt OnClose(GameRecord g, bool paused, bool askOnClose = true)
    {
        if (!askOnClose || paused || g.Status == GameStatus.Never) return ClosePrompt.None;
        if (g.PendingStart is not null) return ClosePrompt.AskStatus;
        return g.Status switch
        {
            GameStatus.Unknown => g.Asked ? ClosePrompt.None : ClosePrompt.AskAddAfterClose,
            GameStatus.Playing => ClosePrompt.AskStatus,
            _ => ClosePrompt.None
        };
    }
}

public static class Actions
{
    public static LogRequest? Apply(GameRecord g, PromptAnswer answer, DateOnly sessionStart, DateOnly today)
    {
        switch (answer)
        {
            case PromptAnswer.Yes:
                g.PendingStart = sessionStart;
                return null;
            case PromptAnswer.Unseen:
                g.Asked = false;
                return null;
            case PromptAnswer.Never:
                g.Status = GameStatus.Never;
                g.PendingStart = null;
                return null;
            case PromptAnswer.AddPlaying:
                return LogRequest.Start(g.Id, sessionStart);
            case PromptAnswer.AddFinished:
                return LogRequest.AddFinished(g.Id, sessionStart, today);
            case PromptAnswer.OpenLog:
                return PendingRequest(g) ?? LogRequest.OpenOnly(g.Id);
            default:
                return null;
        }
    }

    public static LogRequest? PendingRequest(GameRecord g) =>
        g.PendingStart is { } d ? LogRequest.Start(g.Id, d) : null;

    public static LogRequest MarkAs(GameRecord g, PlayedStatus? status, bool? playing, bool? backlog, bool? wishlist, DateOnly today)
    {
        DateOnly? finished = status is PlayedStatus.Completed or PlayedStatus.Retired or PlayedStatus.Abandoned or PlayedStatus.Played ? today : null;
        return new LogRequest(g.Id, status, playing, backlog, wishlist, g.PendingStart, finished);
    }

    public static void OnSavedState(GameRecord g, string? status, bool playingOn)
    {
        if (playingOn) g.Status = GameStatus.Playing;
        else
        {
            switch (status)
            {
                case "completed" or "retired" or "played": g.Status = GameStatus.Finished; break;
                case "shelved" or "abandoned": g.Status = GameStatus.Dropped; break;
            }
        }
        g.PendingStart = null;
    }

    public static void OnSavedFallback(GameRecord g, LogRequest r) =>
        OnSavedState(g, r.Status?.ToString().ToLowerInvariant(), r.Playing == true);
}

public static class Format
{
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    // No-break spaces: "1h 35m" never wraps apart.
    const char Nbsp = '\u00A0';

    public static string Played(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{Nbsp}{t.Minutes}m" : $"{t.Minutes}m";

    // "<Game> closed, 1h 35m": the play time stays on the line with "closed,".
    // Under a minute reads as such here, not "0m" (Tracked games keeps "0m").
    public static string Closed(string name, TimeSpan played) =>
        $"{name} closed,{Nbsp}{(played < TimeSpan.FromMinutes(1) ? $"under{Nbsp}a{Nbsp}minute" : Played(played))}";

    public static string Minutes(int minutes) => Played(TimeSpan.FromMinutes(Math.Max(0, minutes)));

    public static string BackloggdDate(DateOnly d) => d.ToString("MMM d, yyyy", En);

    // "Nov 9" in the current year, "Nov 9, 2025" otherwise, blank when unknown.
    public static string Added(DateTime added, DateTime today) =>
        added == DateTime.MinValue ? "" : added.ToString(added.Year == today.Year ? "MMM d" : "MMM d, yyyy", En);

    public static string Day(DateOnly day, DateTime today) => Added(day.ToDateTime(TimeOnly.MinValue), today);

    // How long ago a popup was missed: "Just now", "5 min ago", "Today 14:05", "Yesterday 21:40",
    // then the day as Added writes it.
    public static string When(DateTime at, DateTime now)
    {
        var ago = now - at;
        if (ago < TimeSpan.FromMinutes(1)) return "Just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min ago";
        if (at.Date == now.Date) return "Today " + at.ToString("HH:mm", En);
        if (at.Date == now.Date.AddDays(-1)) return "Yesterday " + at.ToString("HH:mm", En);
        return Added(at, now);
    }

    // The tray icon's tooltip; Windows cuts it at 63 characters.
    public static string TrayText(bool paused, int missed)
    {
        var notes = new List<string>();
        if (paused) notes.Add("popups paused");
        if (missed > 0) notes.Add($"{missed} missed");
        return notes.Count == 0 ? "Backloggd Logger" : $"Backloggd Logger ({string.Join(", ", notes)})";
    }

    public static string MissedMenu(int count) => count > 0 ? $"Missed popups ({count})..." : "Missed popups...";
}

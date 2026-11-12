using System.Text.Json.Serialization;

namespace BackloggdLogger.Core;

public enum MissedKind { Start, AfterClose, Status, Notice }

// What a notice's first button did, so the same notice can be shown again after a restart.
public enum MissedAction { None, OpenLog, OpenFolder }

// A popup that went away without an answer: it timed out, a fullscreen game hid it, or the app
// closed it. It holds what the popup showed and what its buttons need to work again.
public sealed class MissedPopup
{
    public string Id { get; set; } = "";
    public string? GameId { get; set; }
    public MissedKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime At { get; set; }

    // The session day the question's answers are applied with.
    public DateOnly? SessionStart { get; set; }

    public MissedAction Action { get; set; }

    // The file an Open folder notice points at.
    public string? ActionPath { get; set; }

    // The list's short text. A notice about a game says what to do (its body); the row already
    // names the game. A notice about the app itself is named by its title.
    [JsonIgnore]
    public string What => Kind switch
    {
        MissedKind.Start => "Add as Playing?",
        MissedKind.AfterClose => "Add to Backloggd?",
        MissedKind.Status => "Update your log?",
        _ => GameId is not null && Body.Length > 0 ? Body : Title,
    };

    // One entry per game and kind; notices without a game are told apart by their title.
    internal bool Replaces(MissedPopup other) =>
        Kind == other.Kind && GameId == other.GameId && (GameId is not null || Title == other.Title);
}

public sealed class MissedPopups
{
    public const int Cap = 50;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    List<MissedPopup> _items = new();

    // Newest first.
    public List<MissedPopup> Items
    {
        get => _items;
        set => _items = value ?? new();
    }

    [JsonIgnore]
    public int Count => _items.Count;

    // Raised after the queue's content changed.
    public event Action? Changed;

    public MissedPopup? Find(string id) => _items.Find(m => m.Id == id);

    internal void Add(MissedPopup popup, DateTime now)
    {
        if (popup.Id.Length == 0) popup.Id = Guid.NewGuid().ToString("N");
        popup.At = now;
        _items.RemoveAll(m => m.Replaces(popup));
        _items.Add(popup);
        Trim(now);
        Changed?.Invoke();
    }

    public bool Remove(string id)
    {
        if (_items.RemoveAll(m => m.Id == id) == 0) return false;
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke();
    }

    // Drops entries past the age or the cap, and those of games that are gone or set to Never.
    internal void Prune(IReadOnlyDictionary<string, GameRecord> games, DateTime now)
    {
        var before = _items.Count;
        _items.RemoveAll(m => m.GameId is { } id && (!games.TryGetValue(id, out var g) || g.Status == GameStatus.Never));
        Trim(now);
        if (_items.Count != before) Changed?.Invoke();
    }

    void Trim(DateTime now)
    {
        _items.RemoveAll(m => now - m.At > MaxAge);
        // A stable sort: entries of the same moment keep their order.
        var sorted = _items.OrderByDescending(m => m.At).Take(Cap).ToList();
        _items.Clear();
        _items.AddRange(sorted);
    }
}

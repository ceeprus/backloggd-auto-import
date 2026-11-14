namespace BackloggdLogger.Core;

public abstract record SessionEvent(string GameId);
public sealed record GameStarted(string GameId, DateTime StartedAt) : SessionEvent(GameId);
public sealed record GameStopped(string GameId, DateTime StartedAt, TimeSpan Duration) : SessionEvent(GameId);

// A session still running, as the save file keeps it (UTC), so one the app never saw end is not lost.
public sealed record LiveSession(string GameId, DateTime Start, DateTime LastSeen);

public sealed class SessionTracker
{
    public static readonly TimeSpan DefaultMinSession = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxPollGap = TimeSpan.FromMinutes(2);
    public const int AbsentPollsToStop = 2;

    sealed class Live
    {
        public DateTime Start;
        public DateTime LastSeen;
        public int Absent;
    }

    readonly Dictionary<string, Live> _live = new();

    public TimeSpan MinSession { get; set; } = DefaultMinSession;

    // A game gone for less than this is still the same session, so a crash and relaunch is one session.
    public TimeSpan RestartGrace { get; set; } = TimeSpan.Zero;

    public IReadOnlyList<SessionEvent> Poll(IReadOnlySet<string> running, DateTime now)
    {
        var events = new List<SessionEvent>();

        foreach (var id in running)
        {
            if (_live.TryGetValue(id, out var s) && now - s.LastSeen > MaxPollGap)
            {
                AddStopIfLongEnough(events, id, s);
                _live.Remove(id);
                s = null;
            }
            if (s is null)
            {
                _live[id] = s = new Live { Start = now };
                events.Add(new GameStarted(id, now));
            }
            s.LastSeen = now;
            s.Absent = 0;
        }

        foreach (var (id, s) in _live.ToList())
        {
            if (running.Contains(id)) continue;
            if (++s.Absent < AbsentPollsToStop || now - s.LastSeen < RestartGrace) continue;
            _live.Remove(id);
            AddStopIfLongEnough(events, id, s);
        }

        return events;
    }

    public IReadOnlyList<LiveSession> Snapshot() => _live.Select(kv => new LiveSession(kv.Key, kv.Value.Start, kv.Value.LastSeen)).ToList();

    // Sessions the save file still held at start: the app was killed (forced restart, crash, power cut) while
    // they ran. Each ends at its last sighting, like a session the app saw end.
    public static IReadOnlyList<SessionEvent> Recover(IEnumerable<LiveSession> left, TimeSpan minSession) =>
        left.Where(s => s.LastSeen - s.Start >= minSession).Select(s => (SessionEvent)new GameStopped(s.GameId, s.Start, s.LastSeen - s.Start)).ToList();

    // The app is closing: every game still running ends at its last sighting.
    public IReadOnlyList<SessionEvent> StopAll()
    {
        var events = new List<SessionEvent>();
        foreach (var (id, s) in _live) AddStopIfLongEnough(events, id, s);
        _live.Clear();
        return events;
    }

    void AddStopIfLongEnough(List<SessionEvent> events, string id, Live s)
    {
        var duration = s.LastSeen - s.Start;
        if (duration >= MinSession) events.Add(new GameStopped(id, s.Start, duration));
    }
}

// Whether a game counts as running for app-opened windows: while one is seen, through a one-poll flicker, and
// for the wait after the last sighting. Both must pass, so no setting lets a window open over a game.
public sealed class RunningHold
{
    DateTime? _lastSeen;
    int _misses;

    public bool Update(bool seenNow, DateTime now, TimeSpan wait)
    {
        if (seenNow)
        {
            _lastSeen = now;
            _misses = 0;
            return true;
        }
        if (_lastSeen is null) return false;
        if (_misses < SessionTracker.AbsentPollsToStop) _misses++;
        return _misses < SessionTracker.AbsentPollsToStop || Within(now, wait);
    }

    public bool OnError(DateTime now, TimeSpan wait) => Within(now, wait);

    bool Within(DateTime now, TimeSpan wait) => _lastSeen is { } seen && now - seen < wait;
}

using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class SessionTrackerTests
{
    static readonly DateTime T0 = new(2026, 11, 8, 20, 0, 0);
    readonly SessionTracker _t = new();
    readonly List<SessionEvent> _all = new();

    void Poll(int seconds, params string[] running) =>
        _all.AddRange(_t.Poll(new HashSet<string>(running), T0.AddSeconds(seconds)));

    [Fact]
    public void Started_fires_on_first_sight_with_that_time()
    {
        Poll(0, "steam:1");
        Assert.Equal(new SessionEvent[] { new GameStarted("steam:1", T0) }, _all);
        Poll(5, "steam:1"); Poll(120, "steam:1");
        Assert.Single(_all);
    }

    [Fact]
    public void Short_run_emits_started_but_no_stopped()
    {
        Poll(0, "steam:1"); Poll(20, "steam:1"); Poll(60); Poll(100); Poll(140);
        Assert.Equal(new SessionEvent[] { new GameStarted("steam:1", T0) }, _all);
    }

    [Fact]
    public void Thirty_second_run_emits_stopped()
    {
        Poll(0, "steam:1"); Poll(30, "steam:1"); Poll(32); Poll(34);
        Assert.Equal(new GameStopped("steam:1", T0, TimeSpan.FromSeconds(30)), _all[^1]);
    }

    [Fact]
    public void StopAll_ends_running_games_at_their_last_sighting()
    {
        Poll(0, "steam:1", "steam:2"); Poll(10, "steam:2"); Poll(12); Poll(14); for (var t = 15; t <= 3615; t += 60) Poll(t, "steam:1");
        _all.Clear();
        _all.AddRange(_t.StopAll());
        Assert.Equal(new SessionEvent[] { new GameStopped("steam:1", T0.AddSeconds(15), TimeSpan.FromSeconds(3600)) }, _all);
        Assert.Empty(_t.StopAll());
    }

    [Fact]
    public void StopAll_skips_a_run_shorter_than_the_minimum()
    {
        Poll(0, "steam:1"); Poll(20, "steam:1");
        Assert.Empty(_t.StopAll());
    }

    [Fact]
    public void Two_games_are_tracked_independently()
    {
        for (var s = 0; s <= 150; s += 5) Poll(s, "steam:1", "steam:2");
        Assert.Equal(2, _all.OfType<GameStarted>().Count());
    }

    [Fact]
    public void One_game_stops_while_other_keeps_running()
    {
        for (var s = 0; s <= 300; s += 5) Poll(s, "steam:1", "steam:2");
        Poll(302, "steam:2"); Poll(304, "steam:2");
        var stops = _all.OfType<GameStopped>().ToList();
        Assert.Single(stops);
        Assert.Equal("steam:1", stops[0].GameId);
        Assert.Equal(2, _all.OfType<GameStarted>().Count());
    }

    [Fact]
    public void Gap_ends_session_at_last_seen_and_starts_new_one_immediately()
    {
        for (var s = 0; s <= 300; s += 5) Poll(s, "steam:1");
        Poll(3900, "steam:1");
        Assert.Equal(new GameStopped("steam:1", T0, TimeSpan.FromSeconds(300)), _all[^2]);
        Assert.Equal(new GameStarted("steam:1", T0.AddSeconds(3900)), _all[^1]);
        Assert.Equal(2, _all.OfType<GameStarted>().Count());
    }

    [Fact]
    public void Normal_cadence_never_triggers_gap()
    {
        for (var s = 0; s <= 600; s += 5) Poll(s, "steam:1");
        Assert.Single(_all);
        Assert.IsType<GameStarted>(_all[0]);
    }

    [Fact]
    public void Short_session_cut_by_gap_emits_no_stopped_but_new_started()
    {
        Poll(0, "steam:1"); Poll(5, "steam:1"); Poll(20, "steam:1");
        Poll(3600, "steam:1");
        Assert.Empty(_all.OfType<GameStopped>());
        Assert.Equal(new SessionEvent[]
        {
            new GameStarted("steam:1", T0),
            new GameStarted("steam:1", T0.AddSeconds(3600)),
        }, _all);
    }

    [Fact]
    public void Stopped_fires_on_the_second_consecutive_absent_poll_at_last_seen()
    {
        for (var s = 0; s <= 300; s += 2) Poll(s, "steam:1");
        Assert.Single(_all);
        Poll(302);
        Assert.Single(_all);
        Poll(304);
        Assert.Equal(new GameStopped("steam:1", T0, TimeSpan.FromSeconds(300)), _all[^1]);
        Assert.Equal(2, _all.Count);
    }

    [Fact]
    public void One_poll_flicker_keeps_the_same_session()
    {
        for (var s = 0; s <= 300; s += 2) Poll(s, "steam:1");
        Poll(302); Poll(304, "steam:1");
        for (var s = 306; s <= 400; s += 2) Poll(s, "steam:1");
        Assert.Equal(new SessionEvent[] { new GameStarted("steam:1", T0) }, _all);
        Poll(402); Poll(404);
        Assert.Equal(new GameStopped("steam:1", T0, TimeSpan.FromSeconds(400)), _all[^1]);
    }

    [Fact]
    public void Absent_count_resets_after_flicker()
    {
        for (var s = 0; s <= 300; s += 2) Poll(s, "steam:1");
        Poll(302); Poll(304, "steam:1"); Poll(306);
        Assert.Single(_all);
    }

    [Fact]
    public void Relaunch_after_two_absent_polls_is_a_new_session_with_new_started()
    {
        for (var s = 0; s <= 300; s += 2) Poll(s, "steam:1");
        Poll(302); Poll(304); Poll(306, "steam:1");
        Assert.Equal(new GameStopped("steam:1", T0, TimeSpan.FromSeconds(300)), _all[^2]);
        Assert.Equal(new GameStarted("steam:1", T0.AddSeconds(306)), _all[^1]);
        Assert.Equal(3, _all.Count);
    }

    [Fact]
    public void Stop_is_not_repeated_on_later_absent_polls()
    {
        for (var s = 0; s <= 300; s += 2) Poll(s, "steam:1");
        Poll(302); Poll(304); Poll(306); Poll(308);
        Assert.Single(_all.OfType<GameStopped>());
    }

    [Fact]
    public void Default_min_session_is_thirty_seconds() => Assert.Equal(TimeSpan.FromSeconds(30), new SessionTracker().MinSession);

    [Fact]
    public void Min_session_follows_the_setting()
    {
        _t.MinSession = TimeSpan.FromSeconds(60);
        Poll(0, "steam:1"); Poll(45, "steam:1"); Poll(47); Poll(49);
        Assert.DoesNotContain(_all, e => e is GameStopped);
        Poll(100, "steam:2"); Poll(160, "steam:2"); Poll(162); Poll(164);
        Assert.Equal(new GameStopped("steam:2", T0.AddSeconds(100), TimeSpan.FromSeconds(60)), _all[^1]);
    }
}

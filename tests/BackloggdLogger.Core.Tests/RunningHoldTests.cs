using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class RunningHoldTests
{
    static readonly DateTime T0 = new(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);
    static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);
    readonly RunningHold _h = new();

    [Fact]
    public void Nothing_seen_is_not_running() => Assert.False(_h.Update(false, T0, Ten));

    [Fact]
    public void A_game_on_screen_is_running_even_with_no_wait()
    {
        Assert.True(_h.Update(true, T0, TimeSpan.Zero));
        Assert.True(_h.Update(true, T0.AddSeconds(2), TimeSpan.Zero));
        Assert.True(_h.Update(false, T0.AddSeconds(4), TimeSpan.Zero));
        Assert.False(_h.Update(false, T0.AddSeconds(6), TimeSpan.Zero));
    }

    [Fact]
    public void A_one_poll_flicker_with_no_wait_keeps_the_game_running()
    {
        _h.Update(true, T0, TimeSpan.Zero);
        Assert.True(_h.Update(false, T0.AddSeconds(2), TimeSpan.Zero));
        Assert.True(_h.Update(true, T0.AddSeconds(4), TimeSpan.Zero));
        Assert.True(_h.Update(false, T0.AddSeconds(6), TimeSpan.Zero));
    }

    [Fact]
    public void A_poll_longer_than_the_wait_still_needs_two_misses()
    {
        _h.Update(true, T0, Ten);
        Assert.True(_h.Update(false, T0.AddSeconds(30), Ten));
        Assert.False(_h.Update(false, T0.AddSeconds(60), Ten));
    }

    [Fact]
    public void Holds_for_the_wait_after_the_game_is_gone()
    {
        _h.Update(true, T0, Ten);
        Assert.True(_h.Update(false, T0.AddSeconds(2), Ten));
        Assert.True(_h.Update(false, T0.AddSeconds(4), Ten));
        Assert.True(_h.Update(false, T0.AddSeconds(9), Ten));
        Assert.False(_h.Update(false, T0.AddSeconds(10), Ten));
    }

    // Every allowed poll and wait: the game counts as running until it has been missed on as many
    // polls as a session needs to stop and the wait has passed.
    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 10)]
    [InlineData(2, 0)]
    [InlineData(2, 10)]
    [InlineData(2, 120)]
    [InlineData(10, 10)]
    [InlineData(30, 0)]
    [InlineData(30, 10)]
    [InlineData(30, 120)]
    public void Every_poll_and_wait_holds_through_a_flicker_and_the_wait(int pollSeconds, int waitSeconds)
    {
        var wait = TimeSpan.FromSeconds(waitSeconds);
        Assert.True(_h.Update(true, T0, wait));
        for (var miss = 1; miss <= 200; miss++)
        {
            var elapsed = miss * pollSeconds;
            var want = miss < SessionTracker.AbsentPollsToStop || elapsed < waitSeconds;
            Assert.Equal(want, _h.Update(false, T0.AddSeconds(elapsed), wait));
            if (!want) return;
        }
        Assert.Fail("the hold never ended");
    }

    [Fact]
    public void A_scan_error_keeps_the_hold_from_the_last_sighting()
    {
        _h.Update(true, T0, Ten);
        Assert.True(_h.OnError(T0.AddSeconds(5), Ten));
        Assert.False(_h.OnError(T0.AddSeconds(11), Ten));
        Assert.False(new RunningHold().OnError(T0, Ten));
    }
}

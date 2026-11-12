using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class RulesTests
{
    static readonly DateOnly Start = new(2026, 11, 7);
    static readonly DateOnly Today = new(2026, 11, 8);
    static GameRecord G(GameStatus s = GameStatus.Unknown, DateOnly? pending = null) =>
        new() { Id = "steam:1", DisplayName = "FEZ", Status = s, PendingStart = pending };

    [Theory]
    [InlineData(GameStatus.Unknown, false, StartPrompt.AskAdd)]
    [InlineData(GameStatus.Unknown, true, StartPrompt.None)]
    [InlineData(GameStatus.Playing, false, StartPrompt.None)]
    [InlineData(GameStatus.Never, false, StartPrompt.None)]
    public void OnStart(GameStatus s, bool paused, StartPrompt expected) =>
        Assert.Equal(expected, PromptPolicy.OnStart(G(s), paused));

    [Fact]
    public void OnStart_skips_when_already_asked() =>
        Assert.Equal(StartPrompt.None, PromptPolicy.OnStart(new GameRecord { Id = "steam:1", Asked = true }, false));

    [Fact]
    public void OnClose_skips_unknown_when_already_asked() =>
        Assert.Equal(ClosePrompt.None, PromptPolicy.OnClose(new GameRecord { Id = "steam:1", Asked = true }, false));

    [Fact]
    public void OnStart_skips_when_already_pending() =>
        Assert.Equal(StartPrompt.None, PromptPolicy.OnStart(G(pending: Start), false));

    [Theory]
    [InlineData(GameStatus.Unknown, ClosePrompt.AskAddAfterClose)]
    [InlineData(GameStatus.Playing, ClosePrompt.AskStatus)]
    [InlineData(GameStatus.Finished, ClosePrompt.None)]
    [InlineData(GameStatus.Dropped, ClosePrompt.None)]
    [InlineData(GameStatus.Never, ClosePrompt.None)]
    public void OnClose(GameStatus s, ClosePrompt expected) =>
        Assert.Equal(expected, PromptPolicy.OnClose(G(s), false));

    [Fact]
    public void OnClose_with_pending_asks_status() =>
        Assert.Equal(ClosePrompt.AskStatus, PromptPolicy.OnClose(G(pending: Start), false));

    [Fact]
    public void OpenLog_with_pending_opens_playing_request_with_pending_date() =>
        Assert.Equal(LogRequest.Start("steam:1", Start), Actions.Apply(G(GameStatus.Playing, pending: Start), PromptAnswer.OpenLog, Today, Today));

    [Fact]
    public void MarkAs_with_pending_carries_started_on() =>
        Assert.Equal(Start, Actions.MarkAs(G(pending: Start), PlayedStatus.Completed, false, null, null, Today).StartedOn);

    [Fact]
    public void OnClose_paused_does_nothing() =>
        Assert.Equal(ClosePrompt.None, PromptPolicy.OnClose(G(pending: Start), true));

    [Fact]
    public void Yes_sets_pending_to_session_start()
    {
        var g = G();
        Assert.Null(Actions.Apply(g, PromptAnswer.Yes, Start, Today));
        Assert.Equal(Start, g.PendingStart);
    }

    [Fact]
    public void Never_sets_status()
    {
        var g = G();
        Assert.Null(Actions.Apply(g, PromptAnswer.Never, Start, Today));
        Assert.Equal(GameStatus.Never, g.Status);
    }

    [Fact]
    public void Unseen_unspends_the_question()
    {
        var g = G();
        g.Asked = true;
        Assert.Null(Actions.Apply(g, PromptAnswer.Unseen, Start, Today));
        Assert.False(g.Asked);
        Assert.Null(g.PendingStart);
    }

    [Theory]
    [InlineData(PromptAnswer.NotNow)]
    [InlineData(PromptAnswer.Skip)]
    public void Passive_answers_change_nothing(PromptAnswer a)
    {
        var g = G(GameStatus.Playing);
        Assert.Null(Actions.Apply(g, a, Start, Today));
        Assert.Equal(GameStatus.Playing, g.Status);
        Assert.Null(g.PendingStart);
    }

    [Fact]
    public void Answers_that_open_the_window()
    {
        Assert.Equal(LogRequest.Start("steam:1", Start), Actions.Apply(G(), PromptAnswer.AddPlaying, Start, Today));
        Assert.Equal(LogRequest.AddFinished("steam:1", Start, Today), Actions.Apply(G(), PromptAnswer.AddFinished, Start, Today));
        Assert.Equal(LogRequest.OpenOnly("steam:1"), Actions.Apply(G(GameStatus.Playing), PromptAnswer.OpenLog, Start, Today));
    }

    [Fact]
    public void Factories_set_exact_fields()
    {
        Assert.Equal(new LogRequest("steam:1", null, null, null, null, null, null), LogRequest.OpenOnly("steam:1"));
        Assert.Equal(new LogRequest("steam:1", null, true, null, null, Start, null), LogRequest.Start("steam:1", Start));
        Assert.Equal(new LogRequest("steam:1", PlayedStatus.Completed, false, null, null, Start, Today), LogRequest.AddFinished("steam:1", Start, Today));
    }

    [Theory]
    [InlineData(PlayedStatus.Played, true)]
    [InlineData(PlayedStatus.Completed, true)]
    [InlineData(PlayedStatus.Retired, true)]
    [InlineData(PlayedStatus.Abandoned, true)]
    [InlineData(PlayedStatus.Shelved, false)]
    [InlineData(null, false)]
    public void MarkAs_finished_date_depends_on_status(PlayedStatus? s, bool dated)
    {
        var r = Actions.MarkAs(G(GameStatus.Playing), s, false, true, false, Today);
        Assert.Equal(new LogRequest("steam:1", s, false, true, false, null, dated ? Today : null), r);
    }

    [Fact]
    public void MarkAs_passes_toggles_through_including_untouched_nulls()
    {
        var r = Actions.MarkAs(G(), null, null, false, null, Today);
        Assert.Equal(new LogRequest("steam:1", null, null, false, null, null, null), r);
    }

    [Fact]
    public void Start_shape_is_only_the_start_request()
    {
        Assert.True(LogRequest.Start("steam:1", Start).IsStartShape);
        Assert.False(LogRequest.OpenOnly("steam:1").IsStartShape);
        Assert.False(LogRequest.AddFinished("steam:1", Start, Today).IsStartShape);
        Assert.False(new LogRequest("steam:1", null, true, null, null, null, null).IsStartShape);
        Assert.False(new LogRequest("steam:1", null, true, true, null, Start, null).IsStartShape);
    }

    [Theory]
    [InlineData("completed", false, GameStatus.Finished)]
    [InlineData("retired", false, GameStatus.Finished)]
    [InlineData("played", false, GameStatus.Finished)]
    [InlineData("shelved", false, GameStatus.Dropped)]
    [InlineData("abandoned", false, GameStatus.Dropped)]
    [InlineData("completed", true, GameStatus.Playing)]
    [InlineData(null, true, GameStatus.Playing)]
    [InlineData(null, false, GameStatus.Playing)]
    [InlineData("bogus", false, GameStatus.Playing)]
    public void OnSavedState_maps_what_was_saved(string? status, bool playing, GameStatus expected)
    {
        var g = G(GameStatus.Playing, pending: Start);
        Actions.OnSavedState(g, status, playing);
        Assert.Equal(expected, g.Status);
        Assert.Null(g.PendingStart);
    }

    [Fact]
    public void OnSavedFallback_start_request_makes_a_new_game_playing()
    {
        var g = G(GameStatus.Unknown, pending: Start);
        Actions.OnSavedFallback(g, LogRequest.Start("steam:1", Start));
        Assert.Equal(GameStatus.Playing, g.Status);
        Assert.Null(g.PendingStart);
    }

    [Fact]
    public void OnSavedFallback_add_finished_makes_a_new_game_finished()
    {
        var g = G(GameStatus.Unknown);
        Actions.OnSavedFallback(g, LogRequest.AddFinished("steam:1", Start, Today));
        Assert.Equal(GameStatus.Finished, g.Status);
    }

    [Theory]
    [InlineData(PlayedStatus.Completed, GameStatus.Finished)]
    [InlineData(PlayedStatus.Retired, GameStatus.Finished)]
    [InlineData(PlayedStatus.Played, GameStatus.Finished)]
    [InlineData(PlayedStatus.Shelved, GameStatus.Dropped)]
    [InlineData(PlayedStatus.Abandoned, GameStatus.Dropped)]
    public void OnSavedFallback_uses_the_requested_status(PlayedStatus status, GameStatus expected)
    {
        var g = G(GameStatus.Playing);
        Actions.OnSavedFallback(g, new LogRequest("steam:1", status, false, null, null, null, Today));
        Assert.Equal(expected, g.Status);
    }

    [Fact]
    public void OnSavedFallback_requested_playing_wins_over_status()
    {
        var g = G(GameStatus.Finished);
        Actions.OnSavedFallback(g, new LogRequest("steam:1", PlayedStatus.Completed, true, null, null, null, Today));
        Assert.Equal(GameStatus.Playing, g.Status);
    }

    [Fact]
    public void OnSavedFallback_open_only_keeps_the_status()
    {
        var g = G(GameStatus.Playing, pending: Start);
        Actions.OnSavedFallback(g, LogRequest.OpenOnly("steam:1"));
        Assert.Equal(GameStatus.Playing, g.Status);
        Assert.Null(g.PendingStart);
    }

    [Theory]
    [InlineData(134, "2h\u00A014m")]
    [InlineData(45, "45m")]
    [InlineData(60, "1h\u00A00m")]
    public void Played_format(int minutes, string expected) =>
        Assert.Equal(expected, Format.Played(TimeSpan.FromMinutes(minutes)));

    // "1h 35m" never wraps apart, and never wraps onto a line of its own after "closed,".
    [Fact]
    public void Closed_title_keeps_the_play_time_with_closed()
    {
        Assert.Equal("Hades closed,\u00A01h\u00A035m", Format.Closed("Hades", TimeSpan.FromMinutes(95)));
        Assert.Equal("Hades closed,\u00A012m", Format.Closed("Hades", TimeSpan.FromMinutes(12)));
    }

    [Fact]
    public void Backloggd_date_format() =>
        Assert.Equal("Nov 8, 2026", Format.BackloggdDate(Today));

    [Fact]
    public void OnStart_does_not_ask_when_start_asking_is_off()
    {
        Assert.Equal(StartPrompt.None, PromptPolicy.OnStart(G(), false, askOnStart: false));
        Assert.Equal(StartPrompt.AskAdd, PromptPolicy.OnStart(G(), false, askOnStart: true));
    }

    [Theory]
    [InlineData(GameStatus.Unknown)]
    [InlineData(GameStatus.Playing)]
    public void OnClose_does_not_ask_when_close_asking_is_off(GameStatus s)
    {
        Assert.Equal(ClosePrompt.None, PromptPolicy.OnClose(G(s), false, askOnClose: false));
        Assert.Equal(ClosePrompt.None, PromptPolicy.OnClose(G(pending: Start), false, askOnClose: false));
    }
}

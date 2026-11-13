using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class MarkAsChoiceTests
{
    static readonly DateOnly Today = new(2026, 11, 8);
    static GameRecord G() => new() { Id = "steam:1", DisplayName = "FEZ", Status = GameStatus.Playing };

    // What the close popup hands on (PromptPopup.Selection, then Coordinator.MarkAs).
    static LogRequest Request(MarkAsChoice c) =>
        Actions.MarkAs(G(), c.Status, c.Sent(Shelf.Playing), c.Sent(Shelf.Backlog), c.Sent(Shelf.Wishlist), Today);

    [Fact]
    public void Starts_with_playing_on_and_nothing_changed()
    {
        var c = new MarkAsChoice();
        Assert.Null(c.Status);
        Assert.True(c.IsOn(Shelf.Playing));
        Assert.False(c.IsOn(Shelf.Backlog));
        Assert.False(c.IsOn(Shelf.Wishlist));
        Assert.False(c.Changed);
    }

    [Fact]
    public void Status_pick_sends_playing_false_and_leaves_shelves_null()
    {
        var c = new MarkAsChoice();
        c.Pick(PlayedStatus.Completed);
        Assert.True(c.Changed);
        Assert.Equal(new LogRequest("steam:1", PlayedStatus.Completed, false, null, null, null, Today), Request(c));
    }

    [Fact]
    public void Backlog_only_sends_backlog_and_nothing_else()
    {
        var c = new MarkAsChoice();
        c.Toggle(Shelf.Backlog);
        Assert.Equal(new LogRequest("steam:1", null, null, true, null, null, null), Request(c));
    }

    [Fact]
    public void Toggled_off_again_is_sent_as_false()
    {
        var c = new MarkAsChoice();
        c.Toggle(Shelf.Wishlist);
        c.Toggle(Shelf.Wishlist);
        c.Pick(PlayedStatus.Shelved);
        Assert.Equal(new LogRequest("steam:1", PlayedStatus.Shelved, false, null, false, null, null), Request(c));
    }

    [Fact]
    public void Playing_clicked_off_is_sent_as_false()
    {
        var c = new MarkAsChoice();
        c.Toggle(Shelf.Playing);
        Assert.True(c.Changed);
        Assert.Equal(new LogRequest("steam:1", null, false, null, null, null, null), Request(c));
    }

    [Fact]
    public void Playing_turned_back_on_after_a_status_is_sent_as_true()
    {
        var c = new MarkAsChoice();
        c.Pick(PlayedStatus.Played);
        c.Toggle(Shelf.Playing);
        Assert.Equal(new LogRequest("steam:1", PlayedStatus.Played, true, null, null, null, Today), Request(c));
    }

    [Fact]
    public void Picking_the_same_status_again_clears_it_and_puts_playing_back()
    {
        var c = new MarkAsChoice();
        c.Pick(PlayedStatus.Retired);
        Assert.False(c.IsOn(Shelf.Playing));
        Assert.False(c.Sent(Shelf.Playing));
        c.Pick(PlayedStatus.Retired);
        Assert.Null(c.Status);
        Assert.True(c.IsOn(Shelf.Playing));
        Assert.Null(c.Sent(Shelf.Playing));
        Assert.False(c.Changed);
        Assert.Equal(new LogRequest("steam:1", null, null, null, null, null, null), Request(c));
    }

    [Fact]
    public void Changed_back_to_the_start_is_not_a_change()
    {
        var c = new MarkAsChoice();
        c.Pick(PlayedStatus.Completed);
        c.Pick(PlayedStatus.Completed);
        Assert.False(c.Changed);
        c.Toggle(Shelf.Backlog);
        c.Toggle(Shelf.Backlog);
        Assert.False(c.Changed);
    }

    [Fact]
    public void Playing_turned_off_by_hand_stays_a_change_after_the_status_goes()
    {
        var c = new MarkAsChoice();
        c.Toggle(Shelf.Playing);
        c.Pick(PlayedStatus.Shelved);
        c.Pick(PlayedStatus.Shelved);
        Assert.False(c.IsOn(Shelf.Playing));
        Assert.False(c.Sent(Shelf.Playing));
        Assert.True(c.Changed);
    }

    [Fact]
    public void Playing_turned_back_on_by_hand_after_a_status_is_sent_as_on()
    {
        var c = new MarkAsChoice();
        c.Pick(PlayedStatus.Shelved);
        c.Toggle(Shelf.Playing);
        Assert.True(c.Sent(Shelf.Playing));
        c.Pick(PlayedStatus.Shelved);
        Assert.True(c.IsOn(Shelf.Playing));
        Assert.True(c.Sent(Shelf.Playing));
    }

    [Fact]
    public void A_new_status_replaces_the_old_one()
    {
        var c = new MarkAsChoice();
        c.Pick(PlayedStatus.Completed);
        c.Pick(PlayedStatus.Abandoned);
        Assert.Equal(PlayedStatus.Abandoned, c.Status);
    }

    [Fact]
    public void Sent_reports_null_for_untouched()
    {
        var c = new MarkAsChoice();
        c.Toggle(Shelf.Backlog);
        Assert.Null(c.Sent(Shelf.Playing));
        Assert.True(c.Sent(Shelf.Backlog));
        Assert.Null(c.Sent(Shelf.Wishlist));
    }
}

using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class MissedPopupsTests : IDisposable
{
    static readonly DateTime Now = new(2026, 11, 11, 22, 0, 0, DateTimeKind.Local);
    readonly string _dir = Path.Combine(Path.GetTempPath(), "bl-tests-" + Guid.NewGuid().ToString("N"));
    string StatePath => Path.Combine(_dir, "state.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    static AppState State(params string[] ids)
    {
        var s = new AppState();
        foreach (var id in ids) s.Games[id] = new GameRecord { Id = id, DisplayName = id };
        return s;
    }

    static MissedPopup Start(string game, string title = "FEZ started") =>
        new() { GameId = game, Kind = MissedKind.Start, Title = title, Body = "Add it to Backloggd as Playing?", SessionStart = new DateOnly(2026, 11, 11) };

    static MissedPopup Notice(string title, string? game = null, string body = "") =>
        new() { GameId = game, Kind = MissedKind.Notice, Title = title, Body = body };

    [Fact]
    public void A_recorded_popup_gets_an_id_and_its_time_and_goes_first()
    {
        var s = State("steam:1", "steam:2");
        var a = s.RecordMissed(Start("steam:1"), Now.AddMinutes(-5))!;
        var b = s.RecordMissed(Start("steam:2"), Now)!;
        Assert.NotEqual("", a.Id);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(Now, b.At);
        Assert.Equal(new[] { b.Id, a.Id }, s.MissedPopups.Items.Select(m => m.Id));
        Assert.Equal(2, s.MissedPopups.Count);
    }

    [Fact]
    public void A_newer_popup_of_the_same_kind_for_the_same_game_replaces_the_older_one()
    {
        var s = State("steam:1");
        s.RecordMissed(Start("steam:1", "old"), Now.AddHours(-1));
        s.RecordMissed(new MissedPopup { GameId = "steam:1", Kind = MissedKind.Status, Title = "closed" }, Now.AddMinutes(-30));
        s.RecordMissed(Start("steam:1", "new"), Now);
        Assert.Equal(new[] { "new", "closed" }, s.MissedPopups.Items.Select(m => m.Title));
    }

    [Fact]
    public void Notices_without_a_game_replace_only_the_same_notice()
    {
        var s = State();
        s.RecordMissed(Notice("Settings file was unreadable"), Now.AddMinutes(-3));
        s.RecordMissed(Notice("Stopped adding play time to Backloggd"), Now.AddMinutes(-2));
        s.RecordMissed(Notice("Stopped adding play time to Backloggd"), Now);
        Assert.Equal(new[] { "Stopped adding play time to Backloggd", "Settings file was unreadable" }, s.MissedPopups.Items.Select(m => m.Title));
        Assert.Equal(Now, s.MissedPopups.Items[0].At);
    }

    [Fact]
    public void Nothing_is_recorded_while_popups_are_paused()
    {
        var s = State("steam:1");
        s.PromptsPaused = true;
        Assert.Null(s.RecordMissed(Start("steam:1"), Now));
        Assert.Empty(s.MissedPopups.Items);
    }

    [Fact]
    public void Nothing_is_recorded_for_a_game_that_is_gone_or_set_to_Never()
    {
        var s = State("steam:1");
        s.Games["steam:1"].Status = GameStatus.Never;
        Assert.Null(s.RecordMissed(Start("steam:1"), Now));
        Assert.Null(s.RecordMissed(Start("steam:9"), Now));
        Assert.Empty(s.MissedPopups.Items);
    }

    [Fact]
    public void The_queue_keeps_the_newest_50()
    {
        var s = State();
        for (var i = 0; i < 60; i++) s.RecordMissed(Notice("notice " + i), Now.AddMinutes(i - 60));
        Assert.Equal(MissedPopups.Cap, s.MissedPopups.Count);
        Assert.Equal("notice 59", s.MissedPopups.Items[0].Title);
        Assert.Equal("notice 10", s.MissedPopups.Items[^1].Title);
    }

    [Fact]
    public void Entries_older_than_30_days_drop()
    {
        var s = State();
        s.RecordMissed(Notice("old"), Now.AddDays(-31));
        s.RecordMissed(Notice("kept"), Now.AddDays(-29));
        s.PruneMissed(Now);
        Assert.Equal(new[] { "kept" }, s.MissedPopups.Items.Select(m => m.Title));
    }

    [Fact]
    public void A_game_set_to_Never_or_removed_drops_its_entries()
    {
        var s = State("steam:1", "steam:2", "steam:3");
        s.RecordMissed(Start("steam:1"), Now);
        s.RecordMissed(Start("steam:2"), Now);
        s.RecordMissed(Start("steam:3"), Now);
        s.RecordMissed(Notice("app notice"), Now);
        s.Games["steam:1"].Status = GameStatus.Never;
        s.Games.Remove("steam:2");
        s.PruneMissed(Now);
        Assert.Equal(new string?[] { null, "steam:3" }, s.MissedPopups.Items.Select(m => m.GameId).OrderBy(x => x));
    }

    [Fact]
    public void Remove_and_Clear_take_entries_out()
    {
        var s = State("steam:1");
        var a = s.RecordMissed(Start("steam:1"), Now)!;
        s.RecordMissed(Notice("n"), Now);
        Assert.True(s.MissedPopups.Remove(a.Id));
        Assert.False(s.MissedPopups.Remove(a.Id));
        Assert.Single(s.MissedPopups.Items);
        s.MissedPopups.Clear();
        Assert.Empty(s.MissedPopups.Items);
    }

    [Fact]
    public void Changed_is_raised_only_when_the_queue_changes()
    {
        var s = State("steam:1");
        var n = 0;
        s.MissedPopups.Changed += () => n++;
        var a = s.RecordMissed(Start("steam:1"), Now)!;
        Assert.Equal(1, n);
        s.PruneMissed(Now);
        s.MissedPopups.Remove("nope");
        Assert.Equal(1, n);
        s.MissedPopups.Remove(a.Id);
        Assert.Equal(2, n);
        s.MissedPopups.Clear();
        Assert.Equal(2, n);
    }

    [Fact]
    public void The_queue_round_trips_through_the_state_file()
    {
        var s = State("steam:1");
        s.RecordMissed(new MissedPopup
        {
            GameId = "steam:1", Kind = MissedKind.Notice, Title = "Couldn't add play time to Backloggd for FEZ", Body = "Add 1h 28m for Nov 10 in the log.",
            Action = MissedAction.OpenLog, SessionStart = new DateOnly(2026, 11, 10),
        }, Now);
        s.RecordMissed(new MissedPopup { Kind = MissedKind.Notice, Title = "Settings file was unreadable", Action = MissedAction.OpenFolder, ActionPath = @"C:\x\state.bad.json" }, Now);
        new StateStore(StatePath).Save(s);
        var loaded = new StateStore(StatePath).Load(Now).MissedPopups.Items;
        Assert.Equal(2, loaded.Count);
        var folder = loaded.Single(m => m.Action == MissedAction.OpenFolder);
        Assert.Equal(@"C:\x\state.bad.json", folder.ActionPath);
        var log = loaded.Single(m => m.Action == MissedAction.OpenLog);
        Assert.Equal("steam:1", log.GameId);
        Assert.Equal(MissedKind.Notice, log.Kind);
        Assert.Equal(Now, log.At);
        Assert.Equal(new DateOnly(2026, 11, 10), log.SessionStart);
        Assert.Equal("Add 1h 28m for Nov 10 in the log.", log.Body);
    }

    [Fact]
    public void Loading_drops_what_is_too_old_or_whose_game_is_gone_and_reads_a_file_without_a_queue()
    {
        var s = State("steam:1", "steam:2");
        s.RecordMissed(Start("steam:1"), Now.AddDays(-40));
        s.RecordMissed(Start("steam:2"), Now.AddDays(-1));
        s.RecordMissed(Notice("kept"), Now.AddDays(-1));
        s.Games.Remove("steam:2");
        new StateStore(StatePath).Save(s);
        Assert.Equal(new[] { "kept" }, new StateStore(StatePath).Load(Now).MissedPopups.Items.Select(m => m.Title));
        File.WriteAllText(StatePath, """{ "Games": {}, "MissedPopups": null }""");
        Assert.Empty(new StateStore(StatePath).Load(Now).MissedPopups.Items);
        File.WriteAllText(StatePath, """{ "Games": {}, "MissedPopups": { "Items": null } }""");
        Assert.Empty(new StateStore(StatePath).Load(Now).MissedPopups.Items);
        File.WriteAllText(StatePath, """{ "Games": {} }""");
        Assert.Empty(new StateStore(StatePath).Load(Now).MissedPopups.Items);
    }

    [Theory]
    [InlineData(MissedKind.Start, "steam:1", "FEZ started", "Add it to Backloggd as Playing?", "Add as Playing?")]
    [InlineData(MissedKind.AfterClose, "steam:1", "FEZ closed, 5m", "Add it to Backloggd?", "Add to Backloggd?")]
    [InlineData(MissedKind.Status, "steam:1", "FEZ closed, 5m", "Update your log?", "Update your log?")]
    [InlineData(MissedKind.Notice, "steam:1", "Couldn't add play time to Backloggd for FEZ", "Add 1h 28m for Nov 10 in the log.", "Add 1h 28m for Nov 10 in the log.")]
    [InlineData(MissedKind.Notice, null, "Stopped adding play time to Backloggd", "The app's hidden Backloggd window got focus.", "Stopped adding play time to Backloggd")]
    [InlineData(MissedKind.Notice, "steam:1", "Only a title", "", "Only a title")]
    public void What_is_the_short_text_of_the_popup(MissedKind kind, string? game, string title, string body, string want) =>
        Assert.Equal(want, new MissedPopup { Kind = kind, GameId = game, Title = title, Body = body }.What);

    [Theory]
    [InlineData(0, "Just now")]
    [InlineData(-59, "Just now")]
    [InlineData(30, "Just now")]
    [InlineData(-60, "1 min ago")]
    [InlineData(-5 * 60, "5 min ago")]
    [InlineData(-59 * 60 - 59, "59 min ago")]
    [InlineData(-60 * 60, "Today 21:00")]
    [InlineData(-21 * 3600, "Today 01:00")]
    [InlineData(-22 * 3600 - 20 * 60, "Yesterday 23:40")]
    [InlineData(-46 * 3600, "Yesterday 00:00")]
    [InlineData(-47 * 3600, "Nov 9")]
    public void When_reads_relative_then_as_a_day(int secondsFromNow, string want) =>
        Assert.Equal(want, Format.When(Now.AddSeconds(secondsFromNow), Now));

    [Fact]
    public void When_shows_the_year_for_another_year() =>
        Assert.Equal("Dec 30, 2025", Format.When(new DateTime(2025, 12, 30, 9, 0, 0), Now));

    [Theory]
    [InlineData(false, 0, "Backloggd Logger")]
    [InlineData(false, 3, "Backloggd Logger (3 missed)")]
    [InlineData(true, 0, "Backloggd Logger (popups paused)")]
    [InlineData(true, 3, "Backloggd Logger (popups paused, 3 missed)")]
    [InlineData(true, 50, "Backloggd Logger (popups paused, 50 missed)")]
    public void Tray_text_carries_the_count_within_63_chars(bool paused, int missed, string want)
    {
        Assert.Equal(want, Format.TrayText(paused, missed));
        Assert.True(Format.TrayText(paused, missed).Length <= 63);
    }

    [Theory]
    [InlineData(0, "Missed popups...")]
    [InlineData(1, "Missed popups (1)...")]
    [InlineData(12, "Missed popups (12)...")]
    public void The_menu_item_shows_the_count_above_zero(int count, string want) => Assert.Equal(want, Format.MissedMenu(count));

    // Status column default

    [Fact]
    public void The_Status_column_is_hidden_until_the_user_shows_it()
    {
        var v = new TrackedListView();
        Assert.False(v.Shows(TrackedColumn.Status));
        Assert.All(Enum.GetValues<TrackedColumn>().Where(c => c != TrackedColumn.Status), c => Assert.True(v.Shows(c)));
        var shown = v.Show(TrackedColumn.Status);
        Assert.True(shown.Shows(TrackedColumn.Status));
        Assert.False(shown.Hide(TrackedColumn.Status).Shows(TrackedColumn.Status));
    }

    [Fact]
    public void A_stored_choice_for_Status_is_kept()
    {
        var s = new AppState { Settings = new AppSettings { TrackedList = new TrackedListView().Show(TrackedColumn.Status).Hide(TrackedColumn.Source) } };
        new StateStore(StatePath).Save(s);
        var loaded = new StateStore(StatePath).Load(Now).Settings.TrackedList;
        Assert.True(loaded.Shows(TrackedColumn.Status));
        Assert.False(loaded.Shows(TrackedColumn.Source));

        File.WriteAllText(StatePath, """{ "Settings": { "TrackedList": { "HiddenColumns": "Status" } } }""");
        Assert.False(new StateStore(StatePath).Load(Now).Settings.TrackedList.Shows(TrackedColumn.Status));
        File.WriteAllText(StatePath, """{ "Settings": { "TrackedList": { "HiddenColumns": "Source" } } }""");
        var old = new StateStore(StatePath).Load(Now).Settings.TrackedList;
        Assert.False(old.Shows(TrackedColumn.Status));
        Assert.False(old.Shows(TrackedColumn.Source));
        Assert.True(old.Shows(TrackedColumn.Added));
    }

    [Fact]
    public void Sorting_by_Status_is_kept_while_the_column_is_hidden()
    {
        var v = new TrackedListView { SortColumn = TrackedColumn.Status }.Normalized();
        Assert.Equal(TrackedColumn.Status, v.SortColumn);
        Assert.False(v.Shows(TrackedColumn.Status));
    }
}

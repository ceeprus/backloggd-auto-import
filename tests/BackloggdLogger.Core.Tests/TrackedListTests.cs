using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class TrackedListTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "bl-tests-" + Guid.NewGuid().ToString("N"));
    string StatePath => Path.Combine(_dir, "state.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    // AddedAt

    [Fact]
    public void A_new_record_has_no_added_date() => Assert.Equal(DateTime.MinValue, new GameRecord().AddedAt);

    [Fact]
    public void AddedAt_round_trips()
    {
        var added = new DateTime(2026, 11, 9, 14, 30, 5, DateTimeKind.Local);
        var state = new AppState();
        state.Games["steam:1"] = new GameRecord { Id = "steam:1", DisplayName = "FEZ", AddedAt = added };
        new StateStore(StatePath).Save(state);
        Assert.Equal(added, new StateStore(StatePath).Load().Games["steam:1"].AddedAt);
    }

    [Fact]
    public void An_old_record_with_sessions_loads_its_first_session_day_as_added()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """
            { "Games": { "steam:1": { "Id": "steam:1", "DisplayName": "FEZ",
              "Sessions": [ { "Date": "2026-10-03", "Minutes": 40 }, { "Date": "2026-11-01", "Minutes": 20 } ] } } }
            """);
        Assert.Equal(new DateTime(2026, 10, 3), new StateStore(StatePath).Load().Games["steam:1"].AddedAt);
    }

    [Fact]
    public void An_old_record_without_sessions_loads_the_state_file_time_as_added()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{ "Games": { "steam:1": { "Id": "steam:1", "DisplayName": "FEZ" } } }""");
        var fileTime = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Local);
        File.SetLastWriteTime(StatePath, fileTime);
        Assert.Equal(fileTime, new StateStore(StatePath).Load().Games["steam:1"].AddedAt);
    }

    [Fact]
    public void A_stored_added_date_wins_over_the_fallbacks()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """
            { "Games": { "steam:1": { "Id": "steam:1", "DisplayName": "FEZ", "AddedAt": "2025-05-06T07:08:09",
              "Sessions": [ { "Date": "2026-10-03", "Minutes": 40 } ] } } }
            """);
        Assert.Equal(new DateTime(2025, 5, 6, 7, 8, 9), new StateStore(StatePath).Load().Games["steam:1"].AddedAt);
    }

    [Fact]
    public void FillAddedAt_without_sessions_or_a_fallback_stays_unknown()
    {
        var g = new GameRecord { Id = "steam:1" };
        g.FillAddedAt(null);
        Assert.Equal(DateTime.MinValue, g.AddedAt);
    }

    [Theory]
    [InlineData(2026, 11, 9, "Nov 9")]
    [InlineData(2026, 1, 31, "Jan 31")]
    [InlineData(2025, 11, 9, "Nov 9, 2025")]
    [InlineData(2027, 2, 1, "Feb 1, 2027")]
    public void Added_reads_month_and_day_this_year_and_adds_the_year_otherwise(int y, int m, int d, string want) =>
        Assert.Equal(want, Format.Added(new DateTime(y, m, d, 23, 59, 0), new DateTime(2026, 11, 9)));

    [Fact]
    public void Added_is_blank_when_unknown() => Assert.Equal("", Format.Added(DateTime.MinValue, new DateTime(2026, 11, 9)));

    // Tracked games view settings

    [Fact]
    public void Tracked_list_defaults_hide_emulators_show_every_column_but_Status_and_sort_by_added_newest_first()
    {
        var v = new AppSettings().TrackedList;
        Assert.False(v.ShowEmulators);
        Assert.Equal(TrackedColumns.None, v.HiddenColumns);
        Assert.Equal(TrackedColumn.Added, v.SortColumn);
        Assert.True(v.SortDescending);
        Assert.All(Enum.GetValues<TrackedColumn>(), c => Assert.Equal(c != TrackedColumn.Status, v.Shows(c)));
    }

    [Fact]
    public void An_old_file_without_the_tracked_list_loads_its_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{ "Settings": { "PollSeconds": 5 } }""");
        Assert.Equal(new TrackedListView(), new StateStore(StatePath).Load().Settings.TrackedList);
    }

    [Fact]
    public void Tracked_list_settings_round_trip_with_names_in_the_file()
    {
        var view = new TrackedListView
        {
            ShowEmulators = true,
            HiddenColumns = TrackedColumns.Source | TrackedColumns.Added,
            SortColumn = TrackedColumn.TimePlayed,
            SortDescending = false,
        };
        var state = new AppState { Settings = new AppSettings { PollSeconds = 4, TrackedList = view } };
        new StateStore(StatePath).Save(state);
        var loaded = new StateStore(StatePath).Load().Settings;
        Assert.Equal(state.Settings, loaded);
        Assert.False(loaded.TrackedList.Shows(TrackedColumn.Source));
        Assert.True(loaded.TrackedList.Shows(TrackedColumn.Icon));
        var text = File.ReadAllText(StatePath);
        Assert.Contains("\"TimePlayed\"", text);
        Assert.Contains("Added", text);
        Assert.Contains("Source", text);
    }

    [Fact]
    public void Name_can_not_be_hidden()
    {
        var s = new AppSettings { TrackedList = new TrackedListView { HiddenColumns = TrackedColumns.Name | TrackedColumns.Icon } }.Normalized();
        Assert.True(s.TrackedList.Shows(TrackedColumn.Name));
        Assert.False(s.TrackedList.Shows(TrackedColumn.Icon));
        Assert.Equal(TrackedColumns.Icon, new AppState { Settings = new AppSettings { TrackedList = new TrackedListView { HiddenColumns = TrackedColumns.Name | TrackedColumns.Icon } } }.Settings.TrackedList.HiddenColumns);
    }

    [Fact]
    public void Hide_and_show_flip_one_column()
    {
        var v = new TrackedListView().Hide(TrackedColumn.Status).Hide(TrackedColumn.Name);
        Assert.False(v.Shows(TrackedColumn.Status));
        Assert.True(v.Shows(TrackedColumn.Name));
        Assert.True(v.Show(TrackedColumn.Status).Shows(TrackedColumn.Status));
    }

    [Theory]
    [InlineData("\"Icon, Source\"", TrackedColumns.Icon | TrackedColumns.Source)]
    [InlineData("\"source\"", TrackedColumns.Source)]
    [InlineData("\"Bogus\"", TrackedColumns.None)]
    [InlineData("\"Source, Bogus\"", TrackedColumns.None)]
    [InlineData("65", TrackedColumns.Icon | TrackedColumns.Source)]
    [InlineData("4096", TrackedColumns.None)]
    [InlineData("null", TrackedColumns.None)]
    public void Hidden_columns_read_leniently(string token, TrackedColumns want)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{\"Settings\":{\"TrackedList\":{\"HiddenColumns\":" + token + "}}}");
        var store = new StateStore(StatePath);
        Assert.Equal(want, store.Load().Settings.TrackedList.HiddenColumns);
        Assert.False(store.RecoveredFromCorrupt);
    }

    [Theory]
    [InlineData("\"Bogus\"")]
    [InlineData("\"Icon\"")]
    [InlineData("99")]
    public void A_sort_column_that_can_not_sort_falls_back_to_added(string token)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{\"Settings\":{\"TrackedList\":{\"SortColumn\":" + token + ",\"SortDescending\":false}}}");
        var v = new StateStore(StatePath).Load().Settings.TrackedList;
        Assert.Equal(TrackedColumn.Added, v.SortColumn);
        Assert.False(v.SortDescending);
    }

    [Fact]
    public void Null_tracked_list_becomes_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{ "Settings": { "TrackedList": null } }""");
        Assert.Equal(new TrackedListView(), new StateStore(StatePath).Load().Settings.TrackedList);
    }
}

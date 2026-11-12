using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class PlayHistoryTests
{
    static readonly DateOnly D1 = new(2026, 11, 7);
    static readonly DateOnly D2 = new(2026, 11, 8);

    [Fact]
    public void New_game_has_no_sessions_and_zero_total()
    {
        var g = new GameRecord();
        Assert.Empty(g.Sessions);
        Assert.Equal(0, g.TotalMinutes);
    }

    [Fact]
    public void Same_day_sessions_are_summed_into_one_entry()
    {
        var g = new GameRecord();
        g.AddPlay(D2, 40);
        g.AddPlay(D2, 55);
        Assert.Single(g.Sessions);
        Assert.Equal(95, g.Sessions[0].Minutes);
        Assert.Equal(D2, g.Sessions[0].Date);
        Assert.Equal(95, g.TotalMinutes);
    }

    [Fact]
    public void Different_days_get_their_own_entries_in_date_order()
    {
        var g = new GameRecord();
        g.AddPlay(D2, 10);
        g.AddPlay(D1, 20);
        Assert.Equal(new[] { D1, D2 }, g.Sessions.Select(s => s.Date));
        Assert.Equal(30, g.TotalMinutes);
    }

    [Fact]
    public void Zero_minutes_add_no_entry()
    {
        var g = new GameRecord();
        g.AddPlay(D1, 0);
        Assert.Empty(g.Sessions);
    }

    [Fact]
    public void SetMatch_stores_the_slug_and_the_title()
    {
        var g = new GameRecord();
        g.SetMatch("fez", "FEZ (2012)");
        Assert.Equal("fez", g.Slug);
        Assert.Equal("FEZ (2012)", g.BackloggdTitle);
    }

    [Fact]
    public void SetMatch_to_another_slug_without_a_title_drops_the_old_title()
    {
        var g = new GameRecord();
        g.SetMatch("fez", "FEZ (2012)");
        g.SetMatch("fez-ii", null);
        Assert.Equal("fez-ii", g.Slug);
        Assert.Null(g.BackloggdTitle);
    }

    [Fact]
    public void SetMatch_to_the_same_slug_without_a_title_keeps_the_title()
    {
        var g = new GameRecord();
        g.SetMatch("fez", "FEZ (2012)");
        g.SetMatch("fez", null);
        Assert.Equal("FEZ (2012)", g.BackloggdTitle);
    }

    [Fact]
    public void Negative_minutes_are_ignored()
    {
        var g = new GameRecord();
        g.AddPlay(D1, -5);
        Assert.Empty(g.Sessions);
    }

    [Theory]
    [InlineData(30, 1)]
    [InlineData(29, 0)]
    [InlineData(89, 1)]
    [InlineData(90, 2)]
    [InlineData(5700, 95)]
    public void Session_minutes_round_to_the_nearest_minute(int seconds, int minutes) =>
        Assert.Equal(minutes, PlayHistory.Minutes(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Total_is_not_written_to_the_state_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bl-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "state.json");
            var state = new AppState();
            var g = new GameRecord { Id = "steam:1", DisplayName = "FEZ" };
            g.AddPlay(D1, 20);
            g.AddPlay(D2, 75);
            state.Games[g.Id] = g;
            new StateStore(path).Save(state);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("TotalMinutes", text);
            var loaded = new StateStore(path).Load().Games["steam:1"];
            Assert.Equal(95, loaded.TotalMinutes);
            Assert.Equal(new[] { D1, D2 }, loaded.Sessions.Select(s => s.Date));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Null_sessions_in_the_file_load_as_empty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bl-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "state.json");
            File.WriteAllText(path, """{ "Games": { "steam:1": { "Id": "steam:1", "Sessions": null } } }""");
            var g = new StateStore(path).Load().Games["steam:1"];
            Assert.Empty(g.Sessions);
            g.AddPlay(D1, 5);
            Assert.Equal(5, g.TotalMinutes);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(35, "35m")]
    [InlineData(95, "1h\u00A035m")]
    [InlineData(1505, "25h\u00A05m")]
    public void Total_formats_like_play_time(int minutes, string text) =>
        Assert.Equal(text, Format.Minutes(minutes));
}

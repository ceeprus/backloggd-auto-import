using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class SettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "bl-tests-" + Guid.NewGuid().ToString("N"));
    string StatePath => Path.Combine(_dir, "state.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Defaults()
    {
        var s = new AppSettings();
        Assert.Equal(30, s.MinSessionSeconds);
        Assert.Equal(10, s.StartPopupSeconds);
        Assert.Equal(10, s.ClosePopupSeconds);
        Assert.True(s.AskOnStart);
        Assert.True(s.AskOnClose);
        Assert.Equal(10, s.WindowDelayAfterGameSeconds);
        Assert.Equal(2, s.PollSeconds);
        Assert.Equal(PopupCorner.BottomRight, s.PopupCorner);
        Assert.True(s.AutoAddJournalTime);
        Assert.True(s.DetectEmulators);
    }

    [Fact]
    public void New_state_has_default_settings() => Assert.Equal(new AppSettings(), new AppState().Settings);

    [Fact]
    public void Old_state_file_without_settings_loads_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{ "Games": { "steam:1": { "Id": "steam:1", "DisplayName": "FEZ", "Status": "Playing" } }, "PromptsPaused": true }""");
        var s = new StateStore(StatePath).Load();
        Assert.Equal(new AppSettings(), s.Settings);
        Assert.True(s.PromptsPaused);
        Assert.Empty(s.Games["steam:1"].Sessions);
        Assert.Equal(0, s.Games["steam:1"].TotalMinutes);
    }

    [Fact]
    public void Partial_settings_keep_defaults_for_missing_fields()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{ "Settings": { "PollSeconds": 5, "PopupCorner": "TopRight" } }""");
        var s = new StateStore(StatePath).Load().Settings;
        Assert.Equal(5, s.PollSeconds);
        Assert.Equal(PopupCorner.TopRight, s.PopupCorner);
        Assert.Equal(30, s.MinSessionSeconds);
        Assert.True(s.AskOnClose);
    }

    [Fact]
    public void Null_settings_become_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{ "Settings": null }""");
        Assert.Equal(new AppSettings(), new StateStore(StatePath).Load().Settings);
    }

    [Fact]
    public void Detect_emulators_defaults_on_when_missing_from_the_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{ "Settings": { "PollSeconds": 5 } }""");
        Assert.True(new StateStore(StatePath).Load().Settings.DetectEmulators);
    }

    [Fact]
    public void Out_of_range_values_in_the_file_are_clamped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{ "Settings": { "PollSeconds": 0, "StartPopupSeconds": 9999, "MinSessionSeconds": -4 } }""");
        var s = new StateStore(StatePath).Load().Settings;
        Assert.Equal(SettingRanges.Poll.Min, s.PollSeconds);
        Assert.Equal(SettingRanges.StartPopup.Max, s.StartPopupSeconds);
        Assert.Equal(SettingRanges.MinSession.Min, s.MinSessionSeconds);
    }

    [Fact]
    public void Settings_round_trip()
    {
        var store = new StateStore(StatePath);
        var state = new AppState
        {
            Settings = new AppSettings
            {
                MinSessionSeconds = 60, StartPopupSeconds = 20, ClosePopupSeconds = 30, AskOnStart = false, AskOnClose = false,
                WindowDelayAfterGameSeconds = 0, PollSeconds = 5, PopupCorner = PopupCorner.TopRight, AutoAddJournalTime = true, DetectEmulators = false,
            }
        };
        store.Save(state);
        Assert.Equal(state.Settings, new StateStore(StatePath).Load().Settings);
        Assert.Contains("\"TopRight\"", File.ReadAllText(StatePath));
    }

    [Fact]
    public void Defaults_are_inside_their_ranges()
    {
        var d = new AppSettings();
        Assert.True(SettingRanges.MinSession.Contains(d.MinSessionSeconds));
        Assert.True(SettingRanges.StartPopup.Contains(d.StartPopupSeconds));
        Assert.True(SettingRanges.ClosePopup.Contains(d.ClosePopupSeconds));
        Assert.True(SettingRanges.WindowDelay.Contains(d.WindowDelayAfterGameSeconds));
        Assert.True(SettingRanges.Poll.Contains(d.PollSeconds));
        Assert.Equal(d, d.Normalized());
    }

    [Theory]
    [InlineData("10", null)]
    [InlineData(" 5 ", null)]
    [InlineData("120", null)]
    [InlineData("4", "Use 5 to 120 seconds.")]
    [InlineData("121", "Use 5 to 120 seconds.")]
    [InlineData("", "Use 5 to 120 seconds.")]
    [InlineData("abc", "Use 5 to 120 seconds.")]
    [InlineData("-5", "Use 5 to 120 seconds.")]
    [InlineData("1.5", "Use 5 to 120 seconds.")]
    [InlineData("99999999999", "Use 5 to 120 seconds.")]
    public void Popup_seconds_check(string text, string? error) => Assert.Equal(error, SettingRanges.StartPopup.Check(text));

    [Fact]
    public void Parse_returns_the_number_or_null()
    {
        Assert.Equal(42, SettingRanges.MinSession.Parse(" 42 "));
        Assert.Null(SettingRanges.MinSession.Parse("4"));
        Assert.Null(SettingRanges.MinSession.Parse("x"));
    }

    [Fact]
    public void Hint_names_the_range()
    {
        Assert.Equal("Use 0 to 120 seconds.", SettingRanges.WindowDelay.Hint);
        Assert.Equal("Use 1 to 30 seconds.", SettingRanges.Poll.Hint);
        Assert.Equal("Use 10 to 600 seconds.", SettingRanges.MinSession.Hint);
    }
}

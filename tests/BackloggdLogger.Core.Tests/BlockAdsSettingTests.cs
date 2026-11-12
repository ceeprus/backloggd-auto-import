using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class BlockAdsSettingTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "bl-tests-" + Guid.NewGuid().ToString("N"));
    string StatePath => Path.Combine(_dir, "state.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Ads_are_not_blocked_by_default()
    {
        Assert.False(new AppSettings().BlockAds);
        Assert.False(new AppState().Settings.BlockAds);
        Assert.False(new AppSettings { BlockAds = true }.Normalized() with { BlockAds = false } == new AppSettings { BlockAds = true });
    }

    [Fact]
    public void A_state_file_from_before_the_setting_reads_as_off()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{ "Games": {}, "Settings": { "PollSeconds": 4, "AutoAddJournalTime": true } }""");
        var s = new StateStore(StatePath).Load().Settings;
        Assert.False(s.BlockAds);
        Assert.Equal(4, s.PollSeconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_choice_round_trips(bool on)
    {
        var state = new AppState { Settings = new AppSettings { BlockAds = on } };
        new StateStore(StatePath).Save(state);
        Assert.Equal(on, new StateStore(StatePath).Load().Settings.BlockAds);
    }

    [Fact]
    public void Normalizing_keeps_the_choice() => Assert.True(new AppSettings { BlockAds = true }.Normalized().BlockAds);
}

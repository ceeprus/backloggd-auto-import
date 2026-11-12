namespace BackloggdLogger.Core.Tests;

public class AdHostsTests
{
    [Theory]
    [InlineData("doubleclick.net")]
    [InlineData("googlesyndication.com")]
    [InlineData("venatusmedia.com")]
    [InlineData("nitropay.com")]
    [InlineData("carbonads.net")]
    public void ListedHostIsBlocked(string host) => Assert.True(AdHosts.IsBlocked(host));

    [Theory]
    [InlineData("googleads.g.doubleclick.net")]
    [InlineData("securepubads.g.doubleclick.net")]
    [InlineData("pagead2.googlesyndication.com")]
    [InlineData("edge.venatusmedia.com")]
    [InlineData("hb.vntsm.com")]
    [InlineData("mug.criteo.com")]
    [InlineData("match.adsrvr.org")]
    public void SubdomainOfListedHostIsBlocked(string host) => Assert.True(AdHosts.IsBlocked(host));

    [Theory]
    [InlineData("DoubleClick.NET")]
    [InlineData("ad.doubleclick.net.")]
    [InlineData(" ad.doubleclick.net ")]
    public void CaseTrailingDotAndSpacesDoNotHideAHost(string host) => Assert.True(AdHosts.IsBlocked(host));

    [Theory]
    [InlineData("notdoubleclick.net")]
    [InlineData("doubleclick.net.example.com")]
    [InlineData("notdoubleclick.net.example.com")]
    [InlineData("doubleclick.network")]
    [InlineData("xmedia.net")]
    [InlineData("google.com")]
    [InlineData("www.google.com")]
    [InlineData("accounts.google.com")]
    [InlineData("google-analytics.com.evil.test")]
    public void LookAlikeIsNotBlocked(string host) => Assert.False(AdHosts.IsBlocked(host));

    [Theory]
    [InlineData("backloggd.com")]
    [InlineData("www.backloggd.com")]
    [InlineData("static.backloggd.com")]
    [InlineData("backloggd.b-cdn.net")]
    [InlineData("images.igdb.com")]
    [InlineData("challenges.cloudflare.com")]
    [InlineData("a.nel.cloudflare.com")]
    [InlineData("cdnjs.cloudflare.com")]
    public void HostsTheLogBoxNeedsAreNeverBlocked(string host) => Assert.False(AdHosts.IsBlocked(host));

    [Fact]
    public void NoListedHostCoversANeededHost()
    {
        foreach (var blocked in AdHosts.Blocked)
            foreach (var needed in AdHosts.Needed)
                Assert.False(needed == blocked || needed.EndsWith("." + blocked, StringComparison.Ordinal) || blocked.EndsWith("." + needed, StringComparison.Ordinal),
                    $"{blocked} overlaps {needed}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    public void EmptyHostIsNotBlocked(string? host) => Assert.False(AdHosts.IsBlocked(host));

    [Theory]
    [InlineData("https://securepubads.g.doubleclick.net/tag/js/gpt.js", true)]
    [InlineData("http://www.google-analytics.com/analytics.js", true)]
    [InlineData("wss://ads.pubmatic.com/socket", true)]
    [InlineData("https://backloggd.com/search/games/ad.doubleclick.net/", false)]
    [InlineData("https://backloggd.com/games/hades/?ref=googlesyndication.com", false)]
    [InlineData("https://images.igdb.com/igdb/image/upload/t_cover_big/co1.jpg", false)]
    [InlineData("data:text/plain,doubleclick.net", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void UrlIsJudgedByItsHostOnly(string? url, bool blocked) => Assert.Equal(blocked, AdHosts.IsBlockedUrl(url));

    [Fact]
    public void FilterPatternsCoverEachHostAndItsSubdomains()
    {
        var patterns = AdHosts.FilterPatterns.ToList();
        Assert.Equal(AdHosts.Blocked.Count * 2, patterns.Count);
        Assert.Contains("*://doubleclick.net/*", patterns);
        Assert.Contains("*://*.doubleclick.net/*", patterns);
        Assert.DoesNotContain(patterns, p => p.Contains("backloggd", StringComparison.Ordinal) || p.Contains("cloudflare", StringComparison.Ordinal) || p.Contains("igdb", StringComparison.Ordinal));
    }

    [Fact]
    public void ListHoldsEachHostOnceInLowerCase()
    {
        Assert.Equal(AdHosts.Blocked.Count, AdHosts.Blocked.Distinct().Count());
        Assert.All(AdHosts.Blocked, h => Assert.Equal(h.ToLowerInvariant(), h));
    }

    [Fact]
    public void SlotCssHidesOnlyTheEmptyAdSlotSelectors()
    {
        Assert.Equal(new[] { "[id^=\"div-gpt-ad\"]", ".adsbygoogle", "[data-ad-slot]", "iframe[src*=\"googlesyndication\"]" }, AdHosts.SlotSelectors);
        Assert.Contains("display: none !important", AdHosts.SlotCss, StringComparison.Ordinal);
    }
}

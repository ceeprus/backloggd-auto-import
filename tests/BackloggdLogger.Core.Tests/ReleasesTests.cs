namespace BackloggdLogger.Core.Tests;

public class ReleasesTests
{
    static readonly Version Current = new(1, 3, 0, 0);

    [Theory]
    [InlineData("v1.4.0", "1.4.0")]
    [InlineData("1.3.1", "1.3.1")]
    [InlineData("v2.0", "2.0.0")]
    [InlineData(" V1.10.0 ", "1.10.0")]
    public void A_newer_tag_is_reported(string tag, string want) =>
        Assert.Equal(Version.Parse(want), Releases.Newer(tag, Current));

    [Theory]
    [InlineData("v1.3.0")]
    [InlineData("1.3")]
    [InlineData("v1.2.9")]
    [InlineData("v0.9.0")]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData(null)]
    public void The_same_older_or_unreadable_tag_is_not(string? tag) =>
        Assert.Null(Releases.Newer(tag, Current));

    [Theory]
    [InlineData("https://github.com/ceeprus/backloggd-auto-import/releases/tag/v1.4.0", true)]
    [InlineData("https://github.com/someone/else/releases/tag/v1.4.0", false)]
    [InlineData("http://github.com/ceeprus/backloggd-auto-import/releases/tag/v1.4.0", false)]
    [InlineData("https://github.com/ceeprus/backloggd-auto-import.evil.com/releases/x", false)]
    [InlineData(null, false)]
    public void Only_this_repos_release_pages_are_opened(string? url, bool kept) =>
        Assert.Equal(kept ? url : Releases.Page, Releases.SafeUrl(url));
}

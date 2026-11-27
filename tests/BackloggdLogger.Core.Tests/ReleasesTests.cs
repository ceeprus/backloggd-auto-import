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

    const string Hash = "02377ff87fa66d1cd21d6c2e70d5eee1403b026ada80adf4dc7ab05e4cacd277";

    static System.Text.Json.JsonElement Release(string name, string url, string? digest) =>
        System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            assets = new object[] { new { name = "notes.txt", browser_download_url = url, digest }, new { name, browser_download_url = url, digest } },
        })).RootElement;

    [Fact]
    public void The_exe_and_its_hash_are_read_from_the_release()
    {
        var url = "https://github.com/ceeprus/backloggd-auto-import/releases/download/v1.4.5/BackloggdLogger.exe";
        Assert.Equal((url, Hash), Releases.ExeAsset(Release("BackloggdLogger.exe", url, "sha256:" + Hash.ToUpperInvariant())));
    }

    [Theory]
    [InlineData("Other.exe", "https://github.com/ceeprus/backloggd-auto-import/releases/download/v1/BackloggdLogger.exe", "sha256:" + Hash)]
    [InlineData("BackloggdLogger.exe", "https://evil.example/BackloggdLogger.exe", "sha256:" + Hash)]
    [InlineData("BackloggdLogger.exe", "https://github.com/ceeprus/backloggd-auto-import/releases/download/v1/BackloggdLogger.exe", null)]
    [InlineData("BackloggdLogger.exe", "https://github.com/ceeprus/backloggd-auto-import/releases/download/v1/BackloggdLogger.exe", "md5:abc")]
    [InlineData("BackloggdLogger.exe", "https://github.com/ceeprus/backloggd-auto-import/releases/download/v1/BackloggdLogger.exe", "sha256:zz377ff87fa66d1cd21d6c2e70d5eee1403b026ada80adf4dc7ab05e4cacd277")]
    public void A_release_without_a_checkable_exe_from_this_repo_gives_none(string name, string url, string? digest) =>
        Assert.Null(Releases.ExeAsset(Release(name, url, digest)));

    [Fact]
    public void Only_bytes_with_the_listed_hash_match()
    {
        var data = "hello"u8.ToArray();
        var hash = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
        Assert.True(Releases.HashMatches(data, hash));
        Assert.False(Releases.HashMatches("hellp"u8.ToArray(), hash));
    }
}

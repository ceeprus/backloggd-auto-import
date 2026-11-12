namespace BackloggdLogger.Core.Tests;

public class StorageCapsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "bl-caps-" + Guid.NewGuid().ToString("N"));

    public StorageCapsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

    [Fact]
    public void LogRotatesWhenCapWouldBeExceeded()
    {
        var path = Path.Combine(_dir, "a.log");
        var log = new LogFile(path, 25);
        log.Append("0123456789\n");
        log.Append("abcdefghi\n");
        Assert.False(File.Exists(Path.Combine(_dir, "a.1.log")));
        log.Append("second\n");
        Assert.Equal("0123456789\nabcdefghi\n", File.ReadAllText(Path.Combine(_dir, "a.1.log")));
        Assert.Equal("second\n", File.ReadAllText(path));
        log.Append("0123456789012345\n");
        log.Append("third\n");
        Assert.Equal("second\n0123456789012345\n", File.ReadAllText(Path.Combine(_dir, "a.1.log")));
        Assert.Equal("third\n", File.ReadAllText(path));
    }

    void Make(string rel, int size)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[size]);
    }

    [Fact]
    public void CleanupRemovesOnlyCaches()
    {
        Make(@"EBWebView\Default\Cache\data_0", 1000);
        Make(@"EBWebView\Default\Code Cache\js\x", 500);
        Make(@"EBWebView\Default\GPUCache\y", 300);
        Make(@"EBWebView\Default\DawnGraphiteCache\z", 100);
        Make(@"EBWebView\Default\Service Worker\CacheStorage\c", 200);
        Make(@"EBWebView\Default\Service Worker\Database\keep", 50);
        Make(@"EBWebView\Default\Cookies", 40);
        Make(@"EBWebView\Default\Local Storage\leveldb\keep", 40);
        Make(@"EBWebView\Default\IndexedDB\keep", 40);
        Make(@"EBWebView\Default\Login Data", 40);
        var freed = CacheCleaner.Run(_dir, 500, () => false);
        Assert.Equal(2100, freed);
        Assert.False(Directory.Exists(Path.Combine(_dir, @"EBWebView\Default\Cache")));
        Assert.False(Directory.Exists(Path.Combine(_dir, @"EBWebView\Default\Service Worker\CacheStorage")));
        foreach (var keep in new[] { "Cookies", @"Local Storage\leveldb\keep", @"IndexedDB\keep", "Login Data", @"Service Worker\Database\keep" })
            Assert.True(File.Exists(Path.Combine(_dir, @"EBWebView\Default", keep)), keep);
    }

    [Fact]
    public void CleanupSkipsUnderCapOrInUse()
    {
        Make(@"EBWebView\Default\Cache\data_0", 1000);
        Assert.Equal(-1, CacheCleaner.Run(_dir, 5000, () => false));
        Assert.Equal(-1, CacheCleaner.Run(_dir, 500, () => true));
        Assert.True(File.Exists(Path.Combine(_dir, @"EBWebView\Default\Cache\data_0")));
    }

    [Fact]
    public void LockfileHeldMeansInUse()
    {
        Make(@"EBWebView\lockfile", 1);
        Assert.False(CacheCleaner.InUse(_dir));
        using var held = new FileStream(Path.Combine(_dir, @"EBWebView\lockfile"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(CacheCleaner.InUse(_dir));
    }
}

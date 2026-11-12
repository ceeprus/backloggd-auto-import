namespace BackloggdLogger.Core;

public sealed class LogFile(string path, long maxBytes)
{
    public void Append(string line)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(line);
        var info = new FileInfo(path);
        if (info.Exists && info.Length > 0 && info.Length + bytes > maxBytes)
        {
            var old = Path.ChangeExtension(path, ".1" + Path.GetExtension(path));
            File.Move(path, old, overwrite: true);
        }
        File.AppendAllText(path, line);
    }
}

public static class CacheCleaner
{
    static readonly string[] CacheDirs =
    [
        "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache",
        Path.Combine("Service Worker", "CacheStorage"),
    ];

    public static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { total += f.Length; } catch (Exception) { }
            }
        }
        catch (Exception) { }
        return total;
    }

    public static bool InUse(string userDataRoot)
    {
        var lockFile = Path.Combine(userDataRoot, "EBWebView", "lockfile");
        if (!File.Exists(lockFile)) return false;
        try
        {
            using var fs = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception) { return true; }
    }

    /// <summary>Deletes cache folders when the tree exceeds the cap. Returns bytes freed, or -1 if skipped.</summary>
    public static long Run(string userDataRoot, long capBytes, Func<bool>? inUse = null)
    {
        try
        {
            var ebRoot = Path.Combine(userDataRoot, "EBWebView");
            if (!Directory.Exists(ebRoot)) return -1;
            if (DirSize(userDataRoot) <= capBytes) return -1;
            if (inUse?.Invoke() ?? InUse(userDataRoot)) return -1;
            long freed = 0;
            foreach (var profile in Directory.EnumerateDirectories(ebRoot))
            {
                foreach (var name in CacheDirs)
                {
                    var dir = Path.Combine(profile, name);
                    if (!Directory.Exists(dir)) continue;
                    var before = DirSize(dir);
                    try { Directory.Delete(dir, true); freed += before; }
                    catch (Exception) { freed += before - DirSize(dir); }
                }
            }
            return freed;
        }
        catch (Exception) { return -1; }
    }
}

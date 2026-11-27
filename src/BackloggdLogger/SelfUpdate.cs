using BackloggdLogger.Core;

namespace BackloggdLogger;

// Replaces the running exe with the latest release's. A running exe can be renamed but not overwritten,
// so it steps aside as .old and the next start removes it.
static class SelfUpdate
{
    static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("no exe path");

    static string Fresh => Exe + ".new";

    static string Old => Exe + ".old";

    // Saves the latest release's exe next to this one, only when its bytes match the SHA-256 GitHub lists for it.
    public static async Task DownloadAsync()
    {
        var (_, _, exe) = await UpdateCheck.LatestReleaseAsync();
        if (exe is not { } asset) throw new InvalidOperationException("the release has no exe with a SHA-256");
        using var http = UpdateCheck.Client(TimeSpan.FromMinutes(5));
        var data = await http.GetByteArrayAsync(asset.Url);
        if (!Releases.HashMatches(data, asset.Sha256)) throw new InvalidDataException("the download does not match its SHA-256");
        await File.WriteAllBytesAsync(Fresh, data);
    }

    public static void Discard()
    {
        try { File.Delete(Fresh); }
        catch (Exception ex) { Log.Write("update", "remove download: " + ex.Message); }
    }

    // Puts the downloaded exe in place; on failure the old one goes back.
    public static void Swap()
    {
        File.Delete(Old);
        File.Move(Exe, Old);
        try { File.Move(Fresh, Exe); }
        catch
        {
            File.Move(Old, Exe);
            throw;
        }
    }

    public static void CleanUp()
    {
        try
        {
            File.Delete(Old);
            File.Delete(Fresh);
        }
        catch (Exception ex) { Log.Write("update", "clean up: " + ex.Message); }
    }
}

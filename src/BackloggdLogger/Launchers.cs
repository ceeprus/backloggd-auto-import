using BackloggdLogger.Core;
using Microsoft.Win32;

namespace BackloggdLogger;

interface ILauncherSource
{
    // The games the Epic Games Launcher and GOG Galaxy have installed whose exe is on disk.
    IReadOnlyList<LauncherGame> Installed();
}

// Reads the Epic manifests folder and GOG's registry keys. The list is kept ten minutes; a change in the manifests
// folder, looked for every half minute, reads it again at once. Unreadable entries are skipped and logged once.
sealed class InstalledLauncherGames : ILauncherSource
{
    static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);
    static readonly TimeSpan FolderCheck = TimeSpan.FromSeconds(30);
    const string GogKey = @"SOFTWARE\WOW6432Node\GOG.com\Games";

    static string EpicFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    IReadOnlyList<LauncherGame> _games = [];
    DateTime _readAt = DateTime.MinValue;
    DateTime _folderCheckedAt = DateTime.MinValue;
    DateTime _folderStamp;
    bool _logged;

    public IReadOnlyList<LauncherGame> Installed()
    {
        var now = DateTime.UtcNow;
        var stale = now - _readAt >= KeepFor;
        if (!stale && now - _folderCheckedAt >= FolderCheck)
        {
            _folderCheckedAt = now;
            stale = FolderStamp() != _folderStamp;
        }
        if (!stale) return _games;
        _readAt = now;
        _folderCheckedAt = now;
        _folderStamp = FolderStamp();
        var games = new List<LauncherGame>();
        Read("Epic", () => ReadEpic(games));
        Read("GOG", () => ReadGog(games));
        return _games = games;
    }

    static DateTime FolderStamp()
    {
        try { return Directory.Exists(EpicFolder) ? Directory.GetLastWriteTimeUtc(EpicFolder) : DateTime.MinValue; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    void Read(string launcher, Action read)
    {
        try { read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogOnce($"{launcher} install list: {ex.Message}");
        }
    }

    void LogOnce(string text)
    {
        if (_logged) return;
        _logged = true;
        Log.Write("errors", text);
    }

    void ReadEpic(List<LauncherGame> games)
    {
        if (!Directory.Exists(EpicFolder)) return;
        foreach (var file in Directory.EnumerateFiles(EpicFolder, "*.item"))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogOnce($"Epic manifest {Path.GetFileName(file)}: {ex.Message}");
                continue;
            }
            if (LauncherGames.Epic(text, File.Exists) is { } game) games.Add(game);
        }
    }

    static void ReadGog(List<LauncherGame> games)
    {
        using var root = Registry.LocalMachine.OpenSubKey(GogKey);
        if (root is null) return;
        foreach (var id in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(id);
            if (key is null) continue;
            var values = key.GetValueNames().ToDictionary(n => n, n => key.GetValue(n)?.ToString(), StringComparer.OrdinalIgnoreCase);
            if (LauncherGames.Gog(id, values, File.Exists) is { } game) games.Add(game);
        }
    }
}

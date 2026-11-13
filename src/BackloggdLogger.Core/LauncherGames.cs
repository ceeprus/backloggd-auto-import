using System.Text.Json;

namespace BackloggdLogger.Core;

// A game another launcher has installed: Id is "epic:<AppName>" or "gog:<id>".
public sealed record LauncherGame(string Id, string Name, string ExePath);

// Reads what the Epic Games Launcher and GOG Galaxy keep about installed games. Nothing here touches the disk
// or the registry: text and values are handed in, and exists answers whether an exe is there.
public static class LauncherGames
{
    // One Epic manifest (Data\Manifests\*.item). null for anything that isn't an installed game: an engine or
    // plugin, DLC, an incomplete install, a missing exe, an unreadable file.
    public static LauncherGame? Epic(string itemJson, Func<string, bool> exists)
    {
        try
        {
            using var doc = JsonDocument.Parse(itemJson, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var appName = Text("AppName");
            var location = Text("InstallLocation");
            var launch = Text("LaunchExecutable");
            if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(launch)) return null;
            if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("AppCategories", out var categories) || categories.ValueKind != JsonValueKind.Array
                || !categories.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && string.Equals(c.GetString(), "games", StringComparison.OrdinalIgnoreCase)))
                return null;
            // DLC names the game it belongs to.
            if (Text("MainGameAppName") is { Length: > 0 } main && !string.Equals(main, appName, StringComparison.OrdinalIgnoreCase)) return null;

            var exe = Combine(location, launch);
            if (exe is null || !exists(exe)) return null;
            var name = Text("DisplayName");
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(location.TrimEnd('\\', '/'));
            return new LauncherGame(GameIds.Epic(appName), name.Trim(), exe);
        }
        catch (JsonException) { return null; }
    }

    // One GOG game key (HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\<id>) as its values. null for DLC
    // (dependsOn names its game) and for a game without an exe that is there.
    public static LauncherGame? Gog(string id, IReadOnlyDictionary<string, string?> values, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string? Value(string name) => values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        if (Value("dependsOn") is not null) return null;
        var exe = Value("exe") ?? CommandExe(Value("launchCommand"));
        if (exe is null) return null;
        if (!Path.IsPathRooted(exe))
        {
            if (Value("path") is not { } folder) return null;
            exe = Combine(folder, exe);
        }
        else exe = Normalize(exe);
        if (exe is null || !exists(exe)) return null;
        return new LauncherGame(GameIds.Gog(id.Trim()), Value("gameName") ?? Path.GetFileNameWithoutExtension(exe), exe);
    }

    // One Windows uninstall entry that Battle.net installed (its uninstaller is Blizzard's, with --uid=<game>). The
    // entry names only a launcher stub, so every exe under the install folder that isn't a launcher or a
    // tool counts as the game: one LauncherGame per exe, all with the game's id.
    public static IReadOnlyList<LauncherGame> BattleNet(string key, IReadOnlyDictionary<string, string?> values, Func<string, IEnumerable<string>> exesUnder)
    {
        string? Value(string name) => values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        var uninstall = Value("UninstallString");
        if (uninstall is null || !uninstall.Contains("Blizzard Uninstaller.exe", StringComparison.OrdinalIgnoreCase)) return [];
        var uid = System.Text.RegularExpressions.Regex.Match(uninstall, @"--uid=""?([^""\s]+)").Groups[1].Value;
        if (uid.Length == 0 || uid.Equals("battle.net", StringComparison.OrdinalIgnoreCase)) return [];
        if (Value("InstallLocation") is not { } folder || Normalize(folder) is not { } root) return [];
        var name = Value("DisplayName") ?? key.Trim();
        return exesUnder(root).Where(IsGameExe).Select(exe => new LauncherGame(GameIds.BattleNet(uid), name, exe)).ToList();
    }

    static readonly string[] ToolWords = { "launcher", "battle.net", "crash", "error", "updater", "update", "uninstall", "agent", "browser", "redist", "setup", "helper", "report", "repair" };

    static bool IsGameExe(string exe)
    {
        var file = Path.GetFileNameWithoutExtension(exe);
        return !ToolWords.Any(w => file.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    // The exe of a launch command: the quoted part, or everything up to the first ".exe".
    static string? CommandExe(string? command)
    {
        if (command is null) return null;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : null;
    }

    static string? Combine(string folder, string file)
    {
        try { return Normalize(Path.Combine(folder, file.TrimStart('\\', '/'))); }
        catch (ArgumentException) { return null; }
    }

    static string? Normalize(string path)
    {
        try { return Path.GetFullPath(path.Replace('/', '\\')); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    // The installed games left to this detection: one per exe, without those the user already
    // tracks by that exe (their record stays the game's record).
    public static IReadOnlyList<LauncherGame> Untracked(IEnumerable<LauncherGame> installed, IEnumerable<string> trackedExePaths)
    {
        var taken = new HashSet<string>(trackedExePaths, StringComparer.OrdinalIgnoreCase);
        return installed.Where(g => taken.Add(g.ExePath)).ToList();
    }

    // Those whose exe runs. A game Steam says it is running from the same folder is Steam's.
    public static IReadOnlyList<LauncherGame> Running(IEnumerable<LauncherGame> games, IReadOnlySet<string> runningExePaths, string? steamRunningFolder)
    {
        var steam = steamRunningFolder?.TrimEnd('\\', '/');
        bool UnderSteam(string exe) =>
            !string.IsNullOrEmpty(steam) && exe.Length > steam.Length && exe[steam.Length] is '\\' or '/' && exe.StartsWith(steam, StringComparison.OrdinalIgnoreCase);
        return games.Where(g => runningExePaths.Contains(g.ExePath) && !UnderSteam(g.ExePath)).ToList();
    }
}

using System.Text.RegularExpressions;

namespace BackloggdLogger.Core;

public static partial class SteamFiles
{
    [GeneratedRegex("\"path\"\\s+\"([^\"]*)\"")]
    private static partial Regex PathField();

    [GeneratedRegex("\"name\"\\s+\"([^\"]*)\"")]
    private static partial Regex NameField();

    [GeneratedRegex("\"installdir\"\\s+\"([^\"]*)\"")]
    private static partial Regex InstallDirField();

    public static IReadOnlyList<string> LibraryPaths(string vdfText) =>
        PathField().Matches(vdfText).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).ToList();

    public static string? AppName(string acfText)
    {
        var m = NameField().Match(acfText);
        return m.Success ? m.Groups[1].Value : null;
    }

    // The game's folder name under steamapps/common.
    public static string? InstallDir(string acfText)
    {
        var m = InstallDirField().Match(acfText);
        return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;
    }
}

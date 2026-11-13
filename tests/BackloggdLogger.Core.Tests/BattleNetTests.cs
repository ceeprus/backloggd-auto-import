namespace BackloggdLogger.Core.Tests;

public class BattleNetTests
{
    const string Uninstaller = @"""C:\ProgramData\Battle.net\Agent\Blizzard Uninstaller.exe"" --lang=enUS --uid=prometheus --displayname=""Overwatch""";
    const string Folder = @"C:\Program Files (x86)\Overwatch";

    static Dictionary<string, string?> Entry(string? uninstall = Uninstaller, string? folder = Folder, string? name = "Overwatch") =>
        new(StringComparer.OrdinalIgnoreCase) { ["UninstallString"] = uninstall, ["InstallLocation"] = folder, ["DisplayName"] = name };

    static IEnumerable<string> OverwatchExes(string folder) => new[]
    {
        folder + @"\Overwatch Launcher.exe",
        folder + @"\_retail_\Overwatch.exe",
        folder + @"\_retail_\BlizzardError.exe",
        folder + @"\_retail_\BlizzardBrowser.exe",
        folder + @"\_retail_\Overwatch_crashhandler.exe",
    };

    [Fact]
    public void The_game_exe_counts_and_launchers_and_tools_do_not()
    {
        var games = LauncherGames.BattleNet("Overwatch", Entry(), OverwatchExes);
        Assert.Equal(new[] { new LauncherGame("bnet:prometheus", "Overwatch", Folder + @"\_retail_\Overwatch.exe") }, games);
        Assert.Equal("Battle.net", GameIds.Launcher(games[0].Id));
    }

    [Fact]
    public void Every_game_exe_shares_the_game_id()
    {
        var games = LauncherGames.BattleNet("Diablo IV", Entry(uninstall: Uninstaller.Replace("prometheus", "fenris"), name: "Diablo IV"),
            f => new[] { f + @"\Diablo IV.exe", f + @"\Diablo IV_x64.exe", f + @"\Diablo IV Launcher.exe" });
        Assert.Equal(2, games.Count);
        Assert.All(games, g => Assert.Equal("bnet:fenris", g.Id));
    }

    [Theory]
    [InlineData(@"""C:\ProgramData\Battle.net\Agent\Blizzard Uninstaller.exe"" --lang=enUS --uid=battle.net --displayname=""Battle.net""")]
    [InlineData(@"""C:\Program Files\Some Game\unins000.exe""")]
    [InlineData(@"""C:\ProgramData\Battle.net\Agent\Blizzard Uninstaller.exe"" --lang=enUS")]
    [InlineData(null)]
    public void Battle_net_itself_other_programs_and_entries_without_a_uid_are_skipped(string? uninstall) =>
        Assert.Empty(LauncherGames.BattleNet("x", Entry(uninstall: uninstall), OverwatchExes));

    [Fact]
    public void An_entry_without_an_install_folder_is_skipped() =>
        Assert.Empty(LauncherGames.BattleNet("Overwatch", Entry(folder: null), OverwatchExes));

    [Fact]
    public void The_key_names_a_game_without_a_display_name() =>
        Assert.Equal("Overwatch", LauncherGames.BattleNet("Overwatch", Entry(name: null), OverwatchExes)[0].Name);
}

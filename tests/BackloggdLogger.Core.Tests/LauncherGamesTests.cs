using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class LauncherGamesTests
{
    // The shape of a real Epic manifest (.item), with the fields the launcher writes around the ones read.
    const string EpicItem = """
    {
    	"FormatVersion": 0,
    	"bIsIncompleteInstall": false,
    	"LaunchCommand": "",
    	"LaunchExecutable": "Binaries/Win64/Control_DX12.exe",
    	"ManifestLocation": "C:\\ProgramData\\Epic\\EpicGamesLauncher\\Data\\Manifests",
    	"ManifestHash": "f0e1d2c3b4a5968778695a4b3c2d1e0f12345678",
    	"bIsApplication": true,
    	"bIsExecutable": true,
    	"bIsManaged": false,
    	"bNeedsValidation": false,
    	"bRequiresAuth": true,
    	"bAllowMultipleInstances": false,
    	"bCanRunOffline": true,
    	"BaseURLs": [],
    	"BuildLabel": "Live",
    	"AppCategories": [
    		"public",
    		"games",
    		"applications"
    	],
    	"ChunkDbs": [],
    	"CompatibleApps": [],
    	"DisplayName": "Control",
    	"InstallationGuid": "0123456789ABCDEF0123456789ABCDEF",
    	"InstallLocation": "D:\\Epic Games\\Control",
    	"InstallSessionId": "",
    	"InstallTags": [],
    	"InstallComponents": [],
    	"HostInstallationGuid": "00000000000000000000000000000000",
    	"PrereqIds": [],
    	"StagingLocation": "D:\\Epic Games\\Control/.egstore/bps",
    	"TechnicalType": "",
    	"VaultThumbnailUrl": "",
    	"VaultTitleText": "",
    	"InstallSize": 44000000000,
    	"MainWindowProcessName": "",
    	"ProcessNames": [],
    	"BackgroundProcessNames": [],
    	"MandatoryAppFolderName": "Control",
    	"OwnershipToken": "false",
    	"CatalogNamespace": "calluna",
    	"CatalogItemId": "0a1b2c3d4e5f60718293a4b5c6d7e8f9",
    	"AppName": "Calluna",
    	"AppVersionString": "0.0.344.3537",
    	"MainGameCatalogNamespace": "calluna",
    	"MainGameCatalogItemId": "0a1b2c3d4e5f60718293a4b5c6d7e8f9",
    	"MainGameAppName": "Calluna",
    	"AllowedUriEnvVars": []
    }
    """;

    const string ControlExe = @"D:\Epic Games\Control\Binaries\Win64\Control_DX12.exe";

    static bool Exists(string path) => true;

    static string With(string json, string field, string value) =>
        System.Text.RegularExpressions.Regex.Replace(json, "\"" + field + "\": [^\\n]*", "\"" + field + "\": " + value + ",");

    [Fact]
    public void An_Epic_manifest_gives_the_game_its_id_name_and_exe()
    {
        var g = LauncherGames.Epic(EpicItem, Exists);
        Assert.NotNull(g);
        Assert.Equal("epic:Calluna", g!.Id);
        Assert.Equal("Control", g.Name);
        Assert.Equal(ControlExe, g.ExePath);
    }

    [Fact]
    public void An_Epic_exe_is_looked_for_at_install_location_plus_launch_executable()
    {
        var asked = new List<string>();
        Assert.Null(LauncherGames.Epic(EpicItem, p => { asked.Add(p); return false; }));
        Assert.Equal(new[] { ControlExe }, asked);
    }

    [Fact]
    public void An_incomplete_Epic_install_is_skipped() =>
        Assert.Null(LauncherGames.Epic(With(EpicItem, "bIsIncompleteInstall", "true"), Exists));

    [Theory]
    [InlineData("[\"public\", \"engines\"]")]
    [InlineData("[\"plugins\", \"addons\"]")]
    [InlineData("[]")]
    [InlineData("null")]
    public void An_Epic_entry_that_is_not_a_game_is_skipped(string categories)
    {
        var json = System.Text.RegularExpressions.Regex.Replace(EpicItem, "\"AppCategories\": \\[[^\\]]*\\]", "\"AppCategories\": " + categories);
        Assert.DoesNotContain("\"games\"", json);
        Assert.Null(LauncherGames.Epic(json, Exists));
    }

    [Fact]
    public void Epic_categories_match_whatever_their_case() =>
        Assert.NotNull(LauncherGames.Epic(EpicItem.Replace("\"games\"", "\"Games\""), Exists));

    [Fact]
    public void Epic_DLC_is_skipped_with_or_without_an_executable()
    {
        Assert.Null(LauncherGames.Epic(With(EpicItem, "LaunchExecutable", "\"\""), Exists));
        Assert.Null(LauncherGames.Epic(With(EpicItem, "MainGameAppName", "\"SomeOtherGame\""), Exists));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"DisplayName\": \"Half a file")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{}")]
    [InlineData("{ \"AppName\": 5, \"DisplayName\": [], \"InstallLocation\": {}, \"LaunchExecutable\": true, \"AppCategories\": \"games\" }")]
    public void A_malformed_Epic_manifest_is_skipped_quietly(string json) => Assert.Null(LauncherGames.Epic(json, Exists));

    [Fact]
    public void An_Epic_game_without_a_display_name_is_named_after_its_folder() =>
        Assert.Equal("Control", LauncherGames.Epic(With(EpicItem, "DisplayName", "\"\""), Exists)!.Name);

    // GOG

    static Dictionary<string, string?> Gog(params (string Key, string? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    static readonly Dictionary<string, string?> Witcher = Gog(
        ("gameID", "1207664643"), ("gameName", "The Witcher 3: Wild Hunt"), ("path", @"D:\GOG Games\The Witcher 3 Wild Hunt"),
        ("exe", @"D:\GOG Games\The Witcher 3 Wild Hunt\bin\x64\witcher3.exe"),
        ("launchCommand", "\"D:\\GOG Games\\The Witcher 3 Wild Hunt\\bin\\x64\\witcher3.exe\" -dx12"), ("dependsOn", ""));

    [Fact]
    public void A_GOG_key_gives_the_game_its_id_name_and_exe()
    {
        var g = LauncherGames.Gog("1207664643", Witcher, Exists);
        Assert.NotNull(g);
        Assert.Equal("gog:1207664643", g!.Id);
        Assert.Equal("The Witcher 3: Wild Hunt", g.Name);
        Assert.Equal(@"D:\GOG Games\The Witcher 3 Wild Hunt\bin\x64\witcher3.exe", g.ExePath);
    }

    [Fact]
    public void GOG_DLC_is_skipped()
    {
        var dlc = new Dictionary<string, string?>(Witcher) { ["dependsOn"] = "1207664643" };
        Assert.Null(LauncherGames.Gog("1207664663", dlc, Exists));
    }

    [Theory]
    [InlineData("\"D:\\GOG Games\\Fez\\FEZ.exe\" --gog", @"D:\GOG Games\Fez\FEZ.exe")]
    [InlineData("D:\\GOG Games\\Fez\\FEZ.exe --gog", @"D:\GOG Games\Fez\FEZ.exe")]
    [InlineData("D:\\GOG Games\\Fez\\FEZ.exe", @"D:\GOG Games\Fez\FEZ.exe")]
    [InlineData("FEZ.exe -w", @"D:\GOG Games\Fez\FEZ.exe")]
    public void A_GOG_game_without_exe_takes_it_from_its_launch_command(string command, string want)
    {
        var g = LauncherGames.Gog("1", Gog(("gameName", "FEZ"), ("path", @"D:\GOG Games\Fez"), ("launchCommand", command)), Exists);
        Assert.Equal(want, g?.ExePath);
    }

    [Fact]
    public void A_relative_GOG_exe_is_under_the_game_path() =>
        Assert.Equal(@"D:\GOG Games\Fez\bin\FEZ.exe", LauncherGames.Gog("1", Gog(("gameName", "FEZ"), ("path", @"D:\GOG Games\Fez\"), ("exe", @"bin\FEZ.exe")), Exists)!.ExePath);

    [Fact]
    public void A_GOG_key_without_an_exe_or_whose_exe_is_gone_is_skipped()
    {
        Assert.Null(LauncherGames.Gog("1", Gog(("gameName", "FEZ"), ("path", @"D:\GOG Games\Fez")), Exists));
        Assert.Null(LauncherGames.Gog("1", Gog(("gameName", "FEZ"), ("exe", "FEZ.exe")), Exists));
        Assert.Null(LauncherGames.Gog("1", Witcher, _ => false));
        Assert.Null(LauncherGames.Gog("", Witcher, Exists));
        Assert.Null(LauncherGames.Gog("1", Gog(), Exists));
    }

    [Fact]
    public void A_GOG_game_without_a_name_is_named_after_its_exe() =>
        Assert.Equal("FEZ", LauncherGames.Gog("1", Gog(("exe", @"D:\GOG Games\Fez\FEZ.exe")), Exists)!.Name);

    // Which of them count as running

    static readonly LauncherGame Control = new("epic:Calluna", "Control", ControlExe);
    static readonly LauncherGame Fez = new("gog:1", "FEZ", @"D:\GOG Games\Fez\FEZ.exe");

    static HashSet<string> Paths(params string[] paths) => new(paths, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_game_the_user_tracks_by_the_same_exe_is_left_to_that_record()
    {
        var left = LauncherGames.Untracked(new[] { Control, Fez }, new[] { ControlExe.ToLowerInvariant() });
        Assert.Equal(new[] { Fez }, left);
        Assert.Equal(new[] { Control, Fez }, LauncherGames.Untracked(new[] { Control, Fez }, Array.Empty<string>()));
    }

    [Fact]
    public void The_same_exe_listed_twice_counts_once()
    {
        var twice = new LauncherGame("gog:2", "Control (GOG)", ControlExe.ToUpperInvariant());
        Assert.Equal(new[] { Control }, LauncherGames.Untracked(new[] { Control, twice }, Array.Empty<string>()));
    }

    [Fact]
    public void A_game_counts_as_running_while_its_exe_runs()
    {
        Assert.Equal(new[] { Fez }, LauncherGames.Running(new[] { Control, Fez }, Paths(Fez.ExePath.ToLowerInvariant()), null));
        Assert.Empty(LauncherGames.Running(new[] { Control, Fez }, Paths(), null));
    }

    [Fact]
    public void A_game_Steam_is_running_from_the_same_folder_stays_the_Steam_record()
    {
        var running = Paths(ControlExe, Fez.ExePath);
        Assert.Equal(new[] { Fez }, LauncherGames.Running(new[] { Control, Fez }, running, @"d:\epic games\control"));
        Assert.Equal(new[] { Fez }, LauncherGames.Running(new[] { Control, Fez }, running, @"D:\Epic Games\Control\"));
        // A folder that only starts with the same letters is another game's.
        Assert.Equal(new[] { Control, Fez }, LauncherGames.Running(new[] { Control, Fez }, running, @"D:\Epic Games\Cont"));
    }

    [Theory]
    [InlineData("epic:Calluna", true, false, "Epic")]
    [InlineData("gog:1207664643", false, true, "GOG")]
    [InlineData("steam:570", false, false, null)]
    [InlineData(@"exe:c:\games\fez.exe", false, false, null)]
    public void Ids_say_which_launcher_a_game_came_from(string id, bool epic, bool gog, string? launcher)
    {
        Assert.Equal(epic, GameIds.IsEpic(id));
        Assert.Equal(gog, GameIds.IsGog(id));
        Assert.Equal(launcher, GameIds.Launcher(id));
    }

    [Fact]
    public void Launcher_ids_are_built_from_the_launchers_own_ids()
    {
        Assert.Equal("epic:Calluna", GameIds.Epic("Calluna"));
        Assert.Equal("gog:1207664643", GameIds.Gog("1207664643"));
    }

    [Fact]
    public void Detecting_launcher_games_is_on_by_default_and_round_trips()
    {
        Assert.True(new AppSettings().DetectLauncherGames);
        var dir = Path.Combine(Path.GetTempPath(), "bl-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "state.json");
            new StateStore(path).Save(new AppState { Settings = new AppSettings { DetectLauncherGames = false } });
            Assert.False(new StateStore(path).Load().Settings.DetectLauncherGames);
            File.WriteAllText(path, """{ "Games": {}, "Settings": { "PollSeconds": 4 } }""");
            Assert.True(new StateStore(path).Load().Settings.DetectLauncherGames);
            var state = new AppState();
            state.Games["epic:Calluna"] = new GameRecord { Id = "epic:Calluna", DisplayName = "Control", LauncherExe = ControlExe.ToLowerInvariant() };
            new StateStore(path).Save(state);
            Assert.Equal(ControlExe.ToLowerInvariant(), new StateStore(path).Load().Games["epic:Calluna"].LauncherExe);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

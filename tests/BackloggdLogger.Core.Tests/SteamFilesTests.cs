using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class SteamFilesTests
{
    const string Vdf = """
    "libraryfolders"
    {
        "0"
        {
            "path"		"C:\\Program Files (x86)\\Steam"
            "label"		""
            "apps"
            {
                "228980"		"1234"
            }
        }
        "1"
        {
            "path"		"D:\\SteamLibrary"
        }
    }
    """;

    const string Acf = """
    "AppState"
    {
        "appid"		"224760"
        "name"		"FEZ"
        "installdir"		"FEZ"
    }
    """;

    [Fact]
    public void LibraryPaths_reads_every_path_and_unescapes_backslashes()
    {
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, SteamFiles.LibraryPaths(Vdf));
    }

    [Fact]
    public void AppName_reads_name_field() => Assert.Equal("FEZ", SteamFiles.AppName(Acf));

    [Fact]
    public void AppName_returns_null_without_name() => Assert.Null(SteamFiles.AppName("\"AppState\" { \"appid\" \"1\" }"));

    [Fact]
    public void InstallDir_reads_installdir_field() => Assert.Equal("FEZ", SteamFiles.InstallDir(Acf));

    [Fact]
    public void InstallDir_returns_null_without_it() => Assert.Null(SteamFiles.InstallDir("\"AppState\" { \"name\" \"FEZ\" }"));
}

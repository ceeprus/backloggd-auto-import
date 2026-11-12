using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class GameNamesTests
{
    [Theory]
    [InlineData("Hollow Knight™", "Hollow Knight")]
    [InlineData("Tom Clancy's Rainbow Six® Siege", "Tom Clancy's Rainbow Six Siege")]
    [InlineData("  FEZ  ", "FEZ")]
    [InlineData("FEZ", "FEZ")]
    [InlineData("™®©", "")]
    public void SearchQuery(string name, string expected) =>
        Assert.Equal(expected, GameNames.SearchQuery(name));

    [Theory]
    [InlineData("Paperboat (DirectX 11)", "Paperboat")]
    [InlineData("Game [Vulkan]", "Game")]
    [InlineData("Game (DX12)", "Game")]
    [InlineData("Game (64-bit)", "Game")]
    [InlineData("Game (x64)", "Game")]
    [InlineData("Game (Beta)", "Game")]
    [InlineData("Game (Early Access)", "Game")]
    [InlineData("Game - v1.2.3", "Game")]
    [InlineData("Game v1.2", "Game")]
    [InlineData("Hollow Knight™ (DirectX 11) [x64]", "Hollow Knight")]
    [InlineData("Hollow Knight: Silksong", "Hollow Knight: Silksong")]
    [InlineData("  Some   Game  ", "Some Game")]
    [InlineData("Game (DirectX 11.1)", "Game")]
    [InlineData("Game (DX11.1)", "Game")]
    [InlineData("Game (D3D 12.0)", "Game")]
    [InlineData("Game (Build 1234)", "Game")]
    [InlineData("Game (Steam version)", "Game")]
    [InlineData("Game (2019)", "Game (2019)")]
    public void CleanTitle(string title, string expected) =>
        Assert.Equal(expected, GameNames.CleanTitle(title));

    [Fact]
    public void CandidatesDropTagGroup() =>
        Assert.Equal(new[] { "Paperboat (DirectX 11)", "Paperboat" }, GameNames.SearchCandidates("Paperboat (DirectX 11)"));

    [Fact]
    public void CandidatesSingle() =>
        Assert.Equal(new[] { "Hollow Knight" }, GameNames.SearchCandidates("Hollow Knight™"));

    [Fact]
    public void CandidatesFez() =>
        Assert.Equal(new[] { "FEZ" }, GameNames.SearchCandidates("FEZ"));

    [Fact]
    public void CandidatesSplitOnDash() =>
        Assert.Contains("Some Game", GameNames.SearchCandidates("Some Game - Definitive Edition"));

    [Fact]
    public void CandidatesSplitOnColon() =>
        Assert.Equal(new[] { "Hollow Knight: Silksong", "Hollow Knight" }, GameNames.SearchCandidates("Hollow Knight: Silksong"));

    [Fact]
    public void CandidatesShortPrefixSkipped() =>
        Assert.Equal(new[] { "A: Long Name" }, GameNames.SearchCandidates("A: Long Name"));

    [Fact]
    public void CandidatesFirstTwoWords() =>
        Assert.Equal(new[] { "Alpha Beta Gamma Delta", "Alpha Beta" }, GameNames.SearchCandidates("Alpha Beta Gamma Delta"));

    [Fact]
    public void CandidatesSplitOnPipe() =>
        Assert.Equal(new[] { "Some Game | Launcher", "Some Game" }, GameNames.SearchCandidates("Some Game | Launcher"));

    [Fact]
    public void CandidatesReZeroShortHeadNotCut()
    {
        Assert.Equal(new[] { "Re:Zero" }, GameNames.SearchCandidates("Re:Zero"));
        Assert.DoesNotContain("Re", GameNames.SearchCandidates("Re:Zero Starting Life"));
    }

    [Fact]
    public void PlanMarksShortenedCandidatesExact() =>
        Assert.Equal(
            new[]
            {
                new SearchCandidate("Alpha Beta: Gamma Delta (DX12)", false),
                new SearchCandidate("Alpha Beta: Gamma Delta", false),
                new SearchCandidate("Alpha Beta", true),
            },
            GameNames.SearchPlan("Alpha Beta: Gamma Delta (DX12)"));

    [Fact]
    public void PlanFirstTwoWordsIsExact() =>
        Assert.Equal(
            new[] { new SearchCandidate("Alpha Beta Gamma Delta", false), new SearchCandidate("Alpha Beta", true) },
            GameNames.SearchPlan("Alpha Beta Gamma Delta"));

    [Fact]
    public void PlanCutWithoutGroupsIsExact() =>
        Assert.Equal(
            new[] { new SearchCandidate("Hollow Knight: Silksong", false), new SearchCandidate("Hollow Knight", true) },
            GameNames.SearchPlan("Hollow Knight: Silksong"));

    [Theory]
    [InlineData("Paperboat (DirectX 11)", "Paperboat")]
    [InlineData("Hollow Knight™ (DirectX 11) [x64]", "Hollow Knight")]
    [InlineData("Game - v1.2.3", "Game")]
    [InlineData("Game v1.2", "Game")]
    [InlineData("Game (2019)", "Game (2019)")]
    [InlineData("Hollow Knight: Silksong", "Hollow Knight: Silksong")]
    [InlineData("FEZ", "FEZ")]
    public void PickQuery(string name, string expected) =>
        Assert.Equal(expected, GameNames.PickQuery(name));

    [Theory]
    [InlineData("Hollow Knight - v1.5", "Hollow Knight", "hollow_knight.exe", "Hollow Knight")]
    [InlineData("Celeste (FPS 60)", "Celeste", "celeste-win64.exe", "Celeste")]
    [InlineData("Paperboat (DirectX 11)", "Java(TM) Platform SE binary", "javaw.exe", "Paperboat")]
    [InlineData("Some Game", "OpenJDK Platform binary", "java.exe", "Some Game")]
    [InlineData("Some Game", "Chromium", "game.exe", "Some Game")]
    [InlineData("Some Game", "Unity", "game.exe", "Some Game")]
    [InlineData("Some Game", "Some Game", "SomeGame.exe", "Some Game")]
    [InlineData("Some Game", "Different Product", "game.exe", "Some Game")]
    [InlineData("Game", "game", "game.exe", "Game")]
    [InlineData("Some Game", null, "game.exe", "Some Game")]
    [InlineData("Untitled Window", "nw.js", "nw.exe", "Untitled Window")]
    public void PickName(string title, string? product, string exe, string expected) =>
        Assert.Equal(expected, GameNames.PickName(title, product, exe));

    [Fact]
    public void CandidatesEmpty() =>
        Assert.Empty(GameNames.SearchCandidates("™"));

    [Theory]
    [InlineData("Metal Gear Solid (Disc 2)", "Metal Gear Solid")]
    [InlineData("Final Fantasy VII (Disc 1 of 3)", "Final Fantasy VII")]
    [InlineData("Game [Disc 2]", "Game")]
    [InlineData("Game (CD2)", "Game")]
    [InlineData("Game Disc 2", "Game")]
    [InlineData("Game (Disk 3)", "Game")]
    [InlineData("Game (Disc 2) (USA)", "Game (Disc 2) (USA)")]
    [InlineData("Discworld", "Discworld")]
    [InlineData("Disc 2", "Disc 2")]
    public void CleanTitle_strips_disc_tags(string title, string expected) =>
        Assert.Equal(expected, GameNames.CleanTitle(title));

    [Fact]
    public void DuckStation_discs_share_one_id()
    {
        var emu = new GameRecord { ExePath = @"c:\emu\duckstation-qt-x64-releaseltcg.exe" };
        var d1 = EmulatorTitles.Parse(emu, "Metal Gear Solid (Disc 1)");
        var d2 = EmulatorTitles.Parse(emu, "Metal Gear Solid (Disc 2)");
        Assert.Equal("Metal Gear Solid", d2.Name);
        Assert.Equal(d1.Id, d2.Id);
    }
}

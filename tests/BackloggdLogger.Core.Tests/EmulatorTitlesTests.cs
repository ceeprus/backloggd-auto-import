using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class EmulatorTitlesTests
{
    [Theory]
    [InlineData("Dolphin.exe", EmulatorKind.Dolphin)]
    [InlineData("dolphin.exe", EmulatorKind.Dolphin)]
    [InlineData("pcsx2-qt.exe", EmulatorKind.Pcsx2)]
    [InlineData("pcsx2x64-avx2.exe", EmulatorKind.Pcsx2)]
    [InlineData("PCSX2.exe", EmulatorKind.Pcsx2)]
    [InlineData("duckstation-qt-x64-ReleaseLTCG.exe", EmulatorKind.DuckStation)]
    [InlineData("rpcs3.exe", EmulatorKind.Rpcs3)]
    [InlineData("Ryujinx.exe", EmulatorKind.Ryujinx)]
    [InlineData("Cemu.exe", EmulatorKind.Cemu)]
    [InlineData("PPSSPPWindows64.exe", EmulatorKind.Ppsspp)]
    [InlineData("retroarch.exe", EmulatorKind.RetroArch)]
    public void Detect_by_exe_name(string exe, EmulatorKind want) =>
        Assert.Equal(want, EmulatorTitles.Detect(exe));

    [Theory]
    [InlineData("Celeste.exe")]
    [InlineData("hades.exe")]
    [InlineData("dolphin-tool.exe")]
    [InlineData("rpcs3-updater.exe")]
    [InlineData("")]
    public void Detect_returns_null_for_other_exes(string exe) =>
        Assert.Null(EmulatorTitles.Detect(exe));

    [Theory]
    [InlineData("Dolphin 2412 | JIT64 DC | Direct3D 11 | HLE | The Legend of Zelda: The Wind Waker (GZLE01)", "The Legend of Zelda: The Wind Waker")]
    [InlineData("Dolphin 2503 | JIT64 DC | Vulkan | HLE | Super Mario Galaxy (RMGE01)", "Super Mario Galaxy")]
    [InlineData("Dolphin 5.0-21088 | JITARM64 SC | OpenGL | LLE | Metroid Prime (GM8E01)", "Metroid Prime")]
    [InlineData("Dolphin 2412 | JIT64 DC | Vulkan | HLE | Super Smash Bros. Melee", "Super Smash Bros. Melee")]
    [InlineData("Dolphin 2412", null)]
    [InlineData("Dolphin 5.0-21088", null)]
    [InlineData("Dolphin 2412 | JIT64 DC | Vulkan | HLE", null)]
    [InlineData("Dolphin 5.0 | JIT64 DC | Direct3D 11 | HLE | FPS: 60 - VPS: 60 - 100%", null)]
    [InlineData("Dolphin 2412 | JIT64 DC | Vulkan | HLE | GZLE01", null)]
    [InlineData("Settings", null)]
    [InlineData("Super Mario Sunshine (GMSE01)", null)]
    public void Dolphin(string title, string? want) =>
        Assert.Equal(want, EmulatorTitles.GameFromTitle(EmulatorKind.Dolphin, title));

    [Theory]
    [InlineData("Shadow of the Colossus", "Shadow of the Colossus")]
    [InlineData("Kingdom Hearts II [SLUS-21005]", "Kingdom Hearts II")]
    [InlineData("Ratchet & Clank (SCUS-97199) - PCSX2", "Ratchet & Clank")]
    [InlineData("God of War II - PCSX2 v2.2.0", "God of War II")]
    [InlineData("Okami (USA)", "Okami")]
    [InlineData("PCSX2", null)]
    [InlineData("PCSX2 v2.2.0", null)]
    [InlineData("PCSX2 1.7.5000", null)]
    [InlineData("PCSX2 Settings", null)]
    [InlineData("Log Window - Shadow of the Colossus [SCUS-97472]", null)]
    [InlineData("Controller Settings", null)]
    [InlineData("SLUS-21005", null)]
    [InlineData("Slot: 0 | Speed: 100% (60.00) | Limiter: Nominal | 1.6.0", null)]
    public void Pcsx2(string title, string? want) =>
        Assert.Equal(want, EmulatorTitles.GameFromTitle(EmulatorKind.Pcsx2, title));

    [Theory]
    [InlineData("Final Fantasy VII (USA) (Disc 1)", "Final Fantasy VII")]
    [InlineData("Final Fantasy VII (USA) (Disc 2)", "Final Fantasy VII")]
    [InlineData("Crash Bandicoot [SCUS-94900]", "Crash Bandicoot")]
    [InlineData("Spyro the Dragon - DuckStation", "Spyro the Dragon")]
    [InlineData("Castlevania: Symphony of the Night (SLUS-00067)", "Castlevania: Symphony of the Night")]
    [InlineData("DuckStation", null)]
    [InlineData("DuckStation 0.1-7294", null)]
    [InlineData("DuckStation Settings", null)]
    [InlineData("60.00 FPS", null)]
    public void DuckStation(string title, string? want) =>
        Assert.Equal(want, EmulatorTitles.GameFromTitle(EmulatorKind.DuckStation, title));

    [Theory]
    [InlineData("FPS: 60.00 | Vulkan | 0.0.33-17213-6b2e9e9b Alpha | Persona 5 [NPUB31848]", "Persona 5")]
    [InlineData("FPS: 29.97 | Vulkan | 0.0.31 | Demon's Souls [BLUS30443]", "Demon's Souls")]
    [InlineData("FPS: 30.00 | OpenGL | 0.0.33 | Uncharted: Drake's Fortune [BCUS98103]", "Uncharted: Drake's Fortune")]
    [InlineData("RPCS3 0.0.33-17213-6b2e9e9b Alpha | master", null)]
    [InlineData("RPCS3 0.0.33 Alpha | master | Firmware version: 4.91", null)]
    [InlineData("FPS: 60.00 | Vulkan | 0.0.33", null)]
    [InlineData("FPS: 60.00 | Vulkan | 0.0.33 | [NPUB31848]", null)]
    public void Rpcs3(string title, string? want) =>
        Assert.Equal(want, EmulatorTitles.GameFromTitle(EmulatorKind.Rpcs3, title));

    [Theory]
    [InlineData("Ryujinx 1.1.1376 - The Legend of Zelda: Breath of the Wild v1.6.0 (01007EF00011E000) (64-bit)", "The Legend of Zelda: Breath of the Wild")]
    [InlineData("Ryujinx 1.2.3 - Super Mario Odyssey v1.3.0 (0100000000010000) (64-bit)", "Super Mario Odyssey")]
    [InlineData("Ryujinx 1.1.1376-ldn3.1.3 - Hollow Knight v1.5.12 (0100633007D48000) (64-bit)", "Hollow Knight")]
    [InlineData("Ryujinx 1.2.3 - Xenoblade Chronicles 3 - Future Redeemed v2.2.0 (010074F013262000) (64-bit)", "Xenoblade Chronicles 3 - Future Redeemed")]
    [InlineData("Ryujinx 1.2.3", null)]
    [InlineData("Ryujinx Console 1.2.3", null)]
    [InlineData("Ryujinx 1.2.3 - Settings", null)]
    [InlineData("Ryujinx 1.2.3 - v1.0.0 (0100000000010000) (64-bit)", null)]
    public void Ryujinx(string title, string? want) =>
        Assert.Equal(want, EmulatorTitles.GameFromTitle(EmulatorKind.Ryujinx, title));

    [Theory]
    [InlineData("Cemu 2.0-61 - FPS: 59.94 [TitleId: 00050000-101C9500] The Legend of Zelda Breath of the Wild [EU v208]", "The Legend of Zelda Breath of the Wild")]
    [InlineData("Cemu 2.0 - FPS: 30.00 [Vulkan] [NVIDIA] [TitleId: 00050000-10145D00] Super Mario 3D World [US v80]", "Super Mario 3D World")]
    [InlineData("Cemu 2.0 - FPS: 60.00 [TitleId: 00050000-10176900] Splatoon", "Splatoon")]
    [InlineData("Cemu 2.0", null)]
    [InlineData("Cemu 2.0-61", null)]
    [InlineData("Cemu 2.0-61 - FPS: 59.94 [TitleId: 00050000-101C9500]", null)]
    [InlineData("Cemu 2.0 - FPS: 59.94 [TitleId: 00050000-101C9500] [EU v208]", null)]
    public void Cemu(string title, string? want) =>
        Assert.Equal(want, EmulatorTitles.GameFromTitle(EmulatorKind.Cemu, title));

    [Theory]
    [InlineData("PPSSPP v1.17.1 - ULUS10041 : Lumines", "Lumines")]
    [InlineData("PPSSPP v1.16 - UCUS98632 : Daxter", "Daxter")]
    [InlineData("PPSSPP v1.17.1 - ULUS10391 : Crisis Core: Final Fantasy VII", "Crisis Core: Final Fantasy VII")]
    [InlineData("PPSSPP v1.17.1", null)]
    [InlineData("PPSSPP v1.17.1 - ULUS10041 : ", null)]
    [InlineData("PPSSPP v1.17.1 - ULUS10041", null)]
    public void Ppsspp(string title, string? want) =>
        Assert.Equal(want, EmulatorTitles.GameFromTitle(EmulatorKind.Ppsspp, title));

    [Theory]
    [InlineData("RetroArch - Super Metroid", "Super Metroid")]
    [InlineData("RetroArch Snes9x 1.62.3 c8b1a2f - Chrono Trigger (USA)", "Chrono Trigger")]
    [InlineData("RetroArch Snes9x 1.62.3 - Chrono Trigger (USA) || FPS: 60.0 || Frames: 1200", "Chrono Trigger")]
    [InlineData("RetroArch Snes9x 1.62.3 || FPS: 60.0", null)]
    [InlineData("RetroArch", null)]
    [InlineData("RetroArch 1.19.1", null)]
    public void RetroArch(string title, string? want) =>
        Assert.Equal(want, EmulatorTitles.GameFromTitle(EmulatorKind.RetroArch, title));

    [Theory]
    [InlineData("mgba.exe", "mGBA - Pokemon Emerald (BPEE) - 0.10.3", "Pokemon Emerald")]
    [InlineData("xemu.exe", "xemu | v0.7.120 | Halo: Combat Evolved | 60 FPS", "Halo: Combat Evolved")]
    [InlineData("snes9x-x64.exe", "Snes9x 1.62.3 - Super Metroid", "Super Metroid")]
    [InlineData("citra-qt.exe", "Citra Nightly 2104 | Vulkan | 59.9 FPS | 16.7 ms | 100% | Pokemon Moon [0004000000175E00]", "Pokemon Moon")]
    [InlineData("melonDS.exe", "melonDS 0.9.5", null)]
    [InlineData("emu.exe", "FPS: 60 | Vulkan", null)]
    [InlineData("emu.exe", "60 FPS", null)]
    [InlineData("emu.exe", "1.2.3", null)]
    [InlineData("emu.exe", "v2.0 | Direct3D 12 | JIT", null)]
    [InlineData("emu.exe", "Ys", null)]
    [InlineData("emu.exe", "[SLUS-21005]", null)]
    public void Generic(string exe, string title, string? want) =>
        Assert.Equal(want, EmulatorTitles.GameFromTitle(EmulatorKind.Generic, title, exe));

    [Theory]
    [InlineData(EmulatorKind.Dolphin)]
    [InlineData(EmulatorKind.Pcsx2)]
    [InlineData(EmulatorKind.DuckStation)]
    [InlineData(EmulatorKind.Rpcs3)]
    [InlineData(EmulatorKind.Ryujinx)]
    [InlineData(EmulatorKind.Cemu)]
    [InlineData(EmulatorKind.Ppsspp)]
    [InlineData(EmulatorKind.RetroArch)]
    [InlineData(EmulatorKind.Generic)]
    public void Junk_titles_are_no_game(EmulatorKind kind)
    {
        foreach (var junk in new[] { "", "   ", "FPS: 60.00", "60 FPS", "v1.2.3", "2412", "Vulkan", "100%", "16.7 ms", "|", " - ", "SLUS-21005", "0100000000010000" })
            Assert.Null(EmulatorTitles.GameFromTitle(kind, junk, "emu.exe"));
    }

    [Fact]
    public void Names_go_through_CleanTitle() =>
        Assert.Equal("Hollow Knight", EmulatorTitles.GameFromTitle(EmulatorKind.Pcsx2, "Hollow Knight™ (DirectX 11)"));

    [Theory]
    [InlineData("The Legend of Zelda: The Wind Waker", "the legend of zelda the wind waker")]
    [InlineData("  Ratchet  &  Clank ", "ratchet clank")]
    [InlineData("Pokémon Moon", "pokémon moon")]
    public void Normalized_names(string name, string want) =>
        Assert.Equal(want, GameIds.NormalizeName(name));

    [Fact]
    public void Emulator_game_ids_carry_the_exe_and_the_normalized_name()
    {
        var id = GameIds.Emulator(@"C:\Emulators\Dolphin\Dolphin.exe", "The Legend of Zelda: The Wind Waker");
        Assert.Equal(@"emu:c:\emulators\dolphin\dolphin.exe|the legend of zelda the wind waker", id);
        Assert.Equal(id, GameIds.Emulator(@"c:\emulators\dolphin\dolphin.exe", "the LEGEND of Zelda - The Wind Waker"));
        Assert.True(GameIds.IsEmulatorGame(id));
        Assert.False(GameIds.IsSteam(id));
        Assert.Equal(@"c:\emulators\dolphin\dolphin.exe", GameIds.EmulatorExe(id));
        Assert.Null(GameIds.EmulatorExe("exe:c:\\games\\celeste.exe"));
        Assert.Null(GameIds.EmulatorExe("steam:1"));
    }

    [Fact]
    public void Games_from_different_emulators_are_different_ids() =>
        Assert.NotEqual(GameIds.Emulator(@"C:\a\pcsx2-qt.exe", "Okami"), GameIds.Emulator(@"C:\b\pcsx2-qt.exe", "Okami"));

    [Theory]
    [InlineData(@"c:\emulators\dolphin\dolphin.exe", null, true)]
    [InlineData(@"c:\emulators\dolphin\dolphin.exe", false, false)]
    [InlineData(@"c:\games\celeste\celeste.exe", null, false)]
    [InlineData(@"c:\games\mgba\mgba.exe", true, true)]
    public void A_tracked_exe_is_an_emulator_by_its_name_unless_the_user_chose(string exe, bool? choice, bool want) =>
        Assert.Equal(want, new GameRecord { Id = GameIds.Exe(exe), ExePath = exe, TreatAsEmulator = choice }.IsEmulator);

    [Fact]
    public void Steam_games_and_emulator_games_are_never_emulators()
    {
        Assert.False(new GameRecord { Id = "steam:1", TreatAsEmulator = true }.IsEmulator);
        Assert.False(new GameRecord { Id = GameIds.Emulator(@"c:\d\dolphin.exe", "Okami"), EmulatorExe = @"c:\d\dolphin.exe" }.IsEmulator);
    }

    [Fact]
    public void The_toggle_stores_a_choice_only_when_it_differs_from_the_exe_name()
    {
        var dolphin = new GameRecord { ExePath = @"c:\d\dolphin.exe" };
        EmulatorTitles.SetEmulator(dolphin, false);
        Assert.False(dolphin.IsEmulator);
        Assert.False(dolphin.TreatAsEmulator);
        EmulatorTitles.SetEmulator(dolphin, true);
        Assert.True(dolphin.IsEmulator);
        Assert.Null(dolphin.TreatAsEmulator);

        var mgba = new GameRecord { ExePath = @"c:\m\mgba.exe" };
        EmulatorTitles.SetEmulator(mgba, true);
        Assert.True(mgba.IsEmulator);
        Assert.True(mgba.TreatAsEmulator);
        EmulatorTitles.SetEmulator(mgba, false);
        Assert.False(mgba.IsEmulator);
        Assert.Null(mgba.TreatAsEmulator);
    }

    [Fact]
    public void Kind_comes_from_the_override_then_the_exe_name_then_generic()
    {
        Assert.Equal(EmulatorKind.Dolphin, EmulatorTitles.KindOf(new GameRecord { ExePath = @"c:\d\dolphin.exe" }));
        Assert.Equal(EmulatorKind.Generic, EmulatorTitles.KindOf(new GameRecord { ExePath = @"c:\m\mgba.exe", TreatAsEmulator = true }));
        Assert.Equal(EmulatorKind.Ppsspp, EmulatorTitles.KindOf(new GameRecord { ExePath = @"c:\p\psp.exe", TreatAsEmulator = true, EmulatorKindOverride = EmulatorKind.Ppsspp }));
    }

    [Fact]
    public void Parse_gives_the_game_and_its_id_for_an_emulator_record()
    {
        var dolphin = new GameRecord { Id = GameIds.Exe(@"c:\d\dolphin.exe"), ExePath = @"c:\d\dolphin.exe" };
        var hit = EmulatorTitles.Parse(dolphin, "Dolphin 2412 | JIT64 DC | Vulkan | HLE | Super Mario Sunshine (GMSE01)");
        Assert.Equal("Super Mario Sunshine", hit.Name);
        Assert.Equal(GameIds.Emulator(@"c:\d\dolphin.exe", "Super Mario Sunshine"), hit.Id);
        var idle = EmulatorTitles.Parse(dolphin, "Dolphin 2412");
        Assert.Null(idle.Name);
        Assert.Null(idle.Id);
    }

    [Fact]
    public void Generic_parse_strips_the_exe_name()
    {
        var mgba = new GameRecord { ExePath = @"c:\m\mgba.exe", TreatAsEmulator = true };
        Assert.Equal("Pokemon Emerald", EmulatorTitles.Parse(mgba, "mGBA - Pokemon Emerald (BPEE) - 0.10.3").Name);
    }

    [Fact]
    public void Display_names_for_known_emulators() =>
        Assert.Equal(new[] { "Dolphin", "PCSX2", "DuckStation", "RPCS3", "Ryujinx", "Cemu", "PPSSPP", "RetroArch" },
            new[] { EmulatorKind.Dolphin, EmulatorKind.Pcsx2, EmulatorKind.DuckStation, EmulatorKind.Rpcs3, EmulatorKind.Ryujinx, EmulatorKind.Cemu, EmulatorKind.Ppsspp, EmulatorKind.RetroArch }
                .Select(EmulatorTitles.Name));
}

public class SeenTitlesTests
{
    [Fact]
    public void Each_title_is_new_once_per_emulator()
    {
        var seen = new SeenTitles();
        Assert.True(seen.FirstSight(@"c:\d\dolphin.exe", "Dolphin 2412"));
        Assert.False(seen.FirstSight(@"c:\d\dolphin.exe", "Dolphin 2412"));
        Assert.True(seen.FirstSight(@"c:\p\pcsx2-qt.exe", "Dolphin 2412"));
    }

    [Fact]
    public void Titles_that_differ_only_in_numbers_count_as_one()
    {
        var seen = new SeenTitles();
        Assert.True(seen.FirstSight("e", "FPS: 60.00 | Vulkan | 0.0.33 | Persona 5 [NPUB31848]"));
        Assert.False(seen.FirstSight("e", "FPS: 59.94 | Vulkan | 0.0.33 | Persona 5 [NPUB31848]"));
        Assert.True(seen.FirstSight("e", "FPS: 59.94 | Vulkan | 0.0.33 | Demon's Souls [BLUS30443]"));
    }

    [Fact]
    public void Each_emulator_logs_a_bounded_number_of_titles()
    {
        var seen = new SeenTitles();
        var news = Enumerable.Range(0, SeenTitles.MaxPerEmulator + 50).Count(i => seen.FirstSight("e", $"Game {(char)('a' + i % 26)}{(char)('a' + i / 26 % 26)}{(char)('a' + i / 676)}"));
        Assert.Equal(SeenTitles.MaxPerEmulator, news);
        Assert.True(seen.FirstSight("other", "Game"));
    }
}

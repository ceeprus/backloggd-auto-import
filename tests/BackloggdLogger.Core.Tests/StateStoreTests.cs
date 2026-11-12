using System.Text.Json;
using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class StateStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "bl-tests-" + Guid.NewGuid().ToString("N"));
    string StatePath => Path.Combine(_dir, "state.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Load_without_file_returns_empty_state()
    {
        var s = new StateStore(StatePath).Load();
        Assert.Empty(s.Games);
        Assert.False(s.PromptsPaused);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var store = new StateStore(StatePath);
        var state = new AppState { PromptsPaused = true };
        state.Games["steam:224760"] = new GameRecord
        {
            Id = "steam:224760", DisplayName = "FEZ", Slug = "fez",
            Status = GameStatus.Playing, PendingStart = new DateOnly(2026, 11, 8), LastSessionMinutes = 134
        };
        store.Save(state);

        var loaded = new StateStore(StatePath).Load();
        var g = loaded.Games["steam:224760"];
        Assert.True(loaded.PromptsPaused);
        Assert.Equal("fez", g.Slug);
        Assert.Equal(GameStatus.Playing, g.Status);
        Assert.Equal(new DateOnly(2026, 11, 8), g.PendingStart);
        Assert.Equal(134, g.LastSessionMinutes);
        Assert.Contains("\"Playing\"", File.ReadAllText(StatePath));
    }

    [Fact]
    public void Emulator_fields_round_trip()
    {
        var store = new StateStore(StatePath);
        var state = new AppState();
        const string exe = @"c:\emulators\mgba\mgba.exe";
        state.Games[GameIds.Exe(exe)] = new GameRecord { Id = GameIds.Exe(exe), DisplayName = "mGBA", ExePath = exe, TreatAsEmulator = true, EmulatorKindOverride = EmulatorKind.Generic };
        var gameId = GameIds.Emulator(exe, "Pokemon Emerald");
        state.Games[gameId] = new GameRecord { Id = gameId, DisplayName = "Pokemon Emerald", EmulatorExe = exe };
        store.Save(state);

        var loaded = new StateStore(StatePath).Load();
        var emu = loaded.Games[GameIds.Exe(exe)];
        Assert.True(emu.TreatAsEmulator);
        Assert.True(emu.IsEmulator);
        Assert.Equal(EmulatorKind.Generic, emu.EmulatorKindOverride);
        Assert.Equal(exe, loaded.Games[gameId].EmulatorExe);
        Assert.Contains("\"Generic\"", File.ReadAllText(StatePath));
        Assert.DoesNotContain("IsEmulator", File.ReadAllText(StatePath));
    }

    [Fact]
    public void A_file_without_emulator_fields_reads_them_as_unset()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{"Games":{"exe:c:\\d\\dolphin.exe":{"Id":"exe:c:\\d\\dolphin.exe","DisplayName":"Dolphin","ExePath":"c:\\d\\dolphin.exe"}}}""");
        var g = new StateStore(StatePath).Load().Games.Values.Single();
        Assert.Null(g.TreatAsEmulator);
        Assert.Null(g.EmulatorKindOverride);
        Assert.Null(g.EmulatorExe);
        Assert.True(g.IsEmulator);
    }

    [Fact]
    public void Corrupt_file_is_moved_aside_and_state_starts_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{ not json");
        var store = new StateStore(StatePath);

        var s = store.Load();

        Assert.Empty(s.Games);
        Assert.True(store.RecoveredFromCorrupt);
        Assert.False(File.Exists(StatePath));
        var bad = Assert.Single(Directory.GetFiles(_dir, "state.bad-*.json"));
        Assert.Equal(bad, store.BackupPath);
    }

    [Fact]
    public void Unknown_enum_values_fall_back_to_the_default_and_keep_the_rest()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath,
            "{\"PromptsPaused\":true,\"Settings\":{\"PopupCorner\":\"Left\",\"PollSeconds\":5}," +
            "\"Games\":{\"steam:1\":{\"Id\":\"steam:1\",\"DisplayName\":\"FEZ\",\"Status\":\"Bogus\",\"Slug\":\"fez\"}}}");
        var store = new StateStore(StatePath);

        var s = store.Load();

        Assert.False(store.RecoveredFromCorrupt);
        Assert.True(File.Exists(StatePath));
        Assert.True(s.PromptsPaused);
        Assert.Equal(PopupCorner.BottomRight, s.Settings.PopupCorner);
        Assert.Equal(5, s.Settings.PollSeconds);
        Assert.Equal(GameStatus.Unknown, s.Games["steam:1"].Status);
        Assert.Equal("fez", s.Games["steam:1"].Slug);
    }

    [Theory]
    [InlineData("\"topright\"", PopupCorner.TopRight)]
    [InlineData("1", PopupCorner.TopRight)]
    [InlineData("7", PopupCorner.BottomRight)]
    [InlineData("null", PopupCorner.BottomRight)]
    [InlineData("true", PopupCorner.BottomRight)]
    public void Enum_values_are_read_leniently(string json, PopupCorner want)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{\"Settings\":{\"PopupCorner\":" + json + "}}");
        var store = new StateStore(StatePath);
        Assert.Equal(want, store.Load().Settings.PopupCorner);
        Assert.False(store.RecoveredFromCorrupt);
    }

    [Fact]
    public void Enums_are_still_written_as_names_and_the_match_title_round_trips()
    {
        var store = new StateStore(StatePath);
        var state = new AppState { Settings = new AppSettings { PopupCorner = PopupCorner.TopRight } };
        var g = new GameRecord { Id = "steam:1", DisplayName = "FEZ" };
        g.SetMatch("fez", "FEZ (2012)");
        state.Games[g.Id] = g;
        store.Save(state);
        Assert.Contains("\"TopRight\"", File.ReadAllText(StatePath));
        Assert.Equal("FEZ (2012)", new StateStore(StatePath).Load().Games["steam:1"].BackloggdTitle);
    }

    sealed class NullableProbe
    {
        public PlayedStatus? Status { get; set; }
        public PopupCorner Corner { get; set; } = PopupCorner.TopRight;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(PlayedStatus.Played)]
    [InlineData(PlayedStatus.Abandoned)]
    public void Nullable_enums_round_trip_and_null_stays_null(PlayedStatus? value)
    {
        var json = JsonSerializer.Serialize(new NullableProbe { Status = value }, StateStore.JsonOptions);
        Assert.Equal(value, JsonSerializer.Deserialize<NullableProbe>(json, StateStore.JsonOptions)!.Status);
        Assert.Contains(value is null ? "\"Status\": null" : $"\"Status\": \"{value}\"", json);
    }

    [Theory]
    [InlineData("null", null)]
    [InlineData("\"retired\"", PlayedStatus.Retired)]
    [InlineData("4", PlayedStatus.Abandoned)]
    [InlineData("\"Bogus\"", null)]
    [InlineData("99", null)]
    [InlineData("true", null)]
    public void Nullable_enums_read_leniently(string token, PlayedStatus? want) =>
        Assert.Equal(want, JsonSerializer.Deserialize<NullableProbe>("{\"Status\":" + token + "}", StateStore.JsonOptions)!.Status);

    [Fact]
    public void A_null_plain_enum_still_reads_as_its_default()
    {
        Assert.Equal(PopupCorner.BottomRight, JsonSerializer.Deserialize<NullableProbe>("{\"Corner\":null}", StateStore.JsonOptions)!.Corner);
    }

    [Fact]
    public void Two_corrupt_loads_in_same_second_do_not_collide()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < 3; i++)
        {
            File.WriteAllText(StatePath, "{ not json");
            new StateStore(StatePath).Load();
        }
        Assert.Equal(3, Directory.GetFiles(_dir, "state.bad-*.json").Length);
    }

    [Fact]
    public void Locked_file_fails_load_without_renaming()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{}");
        var store = new StateStore(StatePath);
        AppState s;
        using (new FileStream(StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            s = store.Load();
        Assert.Empty(s.Games);
        Assert.True(store.LoadFailed);
        Assert.True(File.Exists(StatePath));
        Assert.Empty(Directory.GetFiles(_dir, "state.bad-*.json"));
    }

    [Fact]
    public void MarkLoadFailed_makes_save_write_recovered_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{}");
        var store = new StateStore(StatePath);
        store.MarkLoadFailed();
        store.Save(new AppState());
        Assert.True(File.Exists(Path.Combine(_dir, "state.recovered.json")));
        Assert.Equal("{}", File.ReadAllText(StatePath));
    }

    [Fact]
    public void Save_after_failed_load_writes_recovered_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{\"PromptsPaused\":true}");
        var store = new StateStore(StatePath);
        using (new FileStream(StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            store.Load();
        store.Save(new AppState());
        Assert.Equal("{\"PromptsPaused\":true}", File.ReadAllText(StatePath));
        Assert.True(File.Exists(Path.Combine(_dir, "state.recovered.json")));
    }
}

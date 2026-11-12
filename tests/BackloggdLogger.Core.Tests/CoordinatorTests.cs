using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class CoordinatorTests
{
    sealed class FakePrompter : IPrompter
    {
        public readonly List<string> Calls = new();
        public bool Throw;
        public void AskStart(GameRecord g, DateTime s) { if (Throw) throw new InvalidOperationException("popup"); Calls.Add($"start:{g.Id}"); }
        public void AskAfterClose(GameRecord g, TimeSpan p, DateTime s) { if (Throw) throw new InvalidOperationException("popup"); Calls.Add($"afterclose:{g.Id}"); }
        public void AskStatus(GameRecord g, TimeSpan p) => Calls.Add($"status:{g.Id}");
        public void Notify(string text) => Calls.Add($"notify:{text}");
    }

    sealed class FakeOpener : ILogOpener
    {
        public readonly List<LogRequest> Opened = new();
        public void Open(LogRequest r) => Opened.Add(r);
    }

    static readonly DateTime T0 = new(2026, 11, 8, 20, 0, 0);
    static readonly DateOnly Today = new(2026, 11, 8);
    const string Id = "steam:224760";

    readonly AppState _state = new();
    readonly FakePrompter _p = new();
    readonly FakeOpener _o = new();
    int _saves;
    readonly Coordinator _c;


    static readonly DateTime Now = new(2026, 11, 8, 21, 15, 0);

    public CoordinatorTests() => _c = new Coordinator(_state, () => _saves++, _p, _o, () => Today, () => Now);

    void Start() => _c.Handle(new GameStarted(Id, T0), _ => "FEZ");
    void Stop() => _c.Handle(new GameStopped(Id, T0, TimeSpan.FromMinutes(134)), _ => "FEZ");

    const string DuckExe = @"C:\Emu\DuckStation\duckstation-qt-x64-ReleaseLTCG.exe";

    [Fact]
    public void Detected_emulator_is_added_once_as_a_quiet_emulator_record()
    {
        _c.DetectEmulators(new[] { DuckExe });
        var saves = _saves;
        _c.DetectEmulators(new[] { DuckExe });
        var g = Assert.Single(_state.Games.Values);
        Assert.Equal(saves, _saves);
        Assert.True(g.IsEmulator);
        Assert.Equal("DuckStation", g.DisplayName);
        Assert.Equal(DuckExe.ToLowerInvariant(), g.ExePath);
        Assert.Equal(GameIds.Exe(DuckExe), g.Id);
        Assert.True(g.Asked);
        Assert.True(saves > 0);
    }

    [Fact]
    public void Detected_emulator_is_not_added_when_the_setting_is_off()
    {
        _state.Settings = _state.Settings with { DetectEmulators = false };
        _c.DetectEmulators(new[] { DuckExe });
        Assert.Empty(_state.Games);
    }

    [Fact]
    public void Detected_emulator_leaves_an_existing_record_alone()
    {
        var id = GameIds.Exe(DuckExe);
        _state.Games[id] = new GameRecord { Id = id, DisplayName = "Mine", ExePath = DuckExe, TreatAsEmulator = false, Status = GameStatus.Never };
        _c.DetectEmulators(new[] { DuckExe.ToUpperInvariant() });
        var g = Assert.Single(_state.Games.Values);
        Assert.Equal("Mine", g.DisplayName);
        Assert.False(g.IsEmulator);
        Assert.Equal(GameStatus.Never, g.Status);
    }

    [Fact]
    public void Failed_start_popup_does_not_spend_the_question()
    {
        _p.Throw = true;
        Assert.Throws<InvalidOperationException>(Start);
        Assert.False(_state.Games[Id].Asked);
    }

    [Fact]
    public void Failed_close_popup_does_not_spend_the_question()
    {
        Start();
        _state.Games[Id].Asked = false;
        _p.Throw = true;
        Assert.Throws<InvalidOperationException>(Stop);
        Assert.False(_state.Games[Id].Asked);
    }

    [Fact]
    public void Unseen_start_question_is_asked_again_on_close()
    {
        Start();
        Assert.True(_state.Games[Id].Asked);
        _c.Answer(Id, PromptAnswer.Unseen, Today);
        Assert.False(_state.Games[Id].Asked);
        Stop();
        Assert.Equal(new[] { $"start:{Id}", $"afterclose:{Id}" }, _p.Calls);
    }

    [Fact]
    public void New_game_start_creates_record_and_asks()
    {
        Start();
        Assert.Equal("FEZ", _state.Games[Id].DisplayName);
        Assert.Equal(new[] { $"start:{Id}" }, _p.Calls);
        Assert.True(_saves > 0);
    }

    [Fact]
    public void Yes_then_close_shows_status_popup_and_opens_no_window()
    {
        Start();
        _c.Answer(Id, PromptAnswer.Yes, Today);
        Stop();
        Assert.Equal(new[] { $"start:{Id}", $"status:{Id}" }, _p.Calls);
        Assert.Empty(_o.Opened);
        Assert.Equal(Today, _state.Games[Id].PendingStart);
        Assert.Equal(134, _state.Games[Id].LastSessionMinutes);
    }

    [Fact]
    public void Pending_close_popup_open_log_enqueues_pending_request()
    {
        Start();
        _c.Answer(Id, PromptAnswer.Yes, Today);
        Stop();
        _c.Answer(Id, PromptAnswer.OpenLog, Today);
        Assert.Equal(new[] { LogRequest.Start(Id, Today) }, _o.Opened);
    }

    [Fact]
    public void Pending_close_popup_mark_as_includes_started_on()
    {
        Start();
        _c.Answer(Id, PromptAnswer.Yes, Today);
        Stop();
        _c.MarkAs(Id, PlayedStatus.Completed, false, null, null);
        Assert.Equal(new LogRequest(Id, PlayedStatus.Completed, false, null, null, Today, Today), _o.Opened[^1]);
    }

    [Fact]
    public void Pending_close_popup_skip_keeps_pending_start()
    {
        Start();
        _c.Answer(Id, PromptAnswer.Yes, Today);
        Stop();
        _c.Answer(Id, PromptAnswer.Skip, Today);
        Assert.Equal(Today, _state.Games[Id].PendingStart);
        Assert.Empty(_o.Opened);
    }

    [Fact]
    public void Ignored_start_ask_is_not_repeated_on_close_or_later_launches()
    {
        Start();
        Stop();
        Start();
        Stop();
        Assert.Equal(new[] { $"start:{Id}" }, _p.Calls);
        Assert.True(_state.Games[Id].Asked);
    }

    [Fact]
    public void Ask_on_close_when_start_was_missed_counts_as_the_one_ask()
    {
        Stop();
        Assert.Equal(new[] { $"afterclose:{Id}" }, _p.Calls);
        Assert.True(_state.Games[Id].Asked);
        Start();
        Stop();
        Assert.Equal(new[] { $"afterclose:{Id}" }, _p.Calls);
    }

    [Fact]
    public void Reset_asked_flag_asks_again()
    {
        Start();
        Stop();
        _state.Games[Id].Asked = false;
        Start();
        Assert.Equal($"start:{Id}", _p.Calls[^1]);
        Assert.Equal(2, _p.Calls.Count);
    }

    [Fact]
    public void Playing_game_still_gets_status_prompt_when_asked_flag_set()
    {
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "FEZ", Status = GameStatus.Playing, Asked = true };
        Start();
        Stop();
        Assert.Equal(new[] { $"status:{Id}" }, _p.Calls);
    }

    [Fact]
    public void After_save_next_close_asks_status_and_mark_as_opens_completed()
    {
        Start();
        _c.Answer(Id, PromptAnswer.Yes, Today);
        Stop();
        _c.Saved(Id, null, true);
        Assert.Equal(GameStatus.Playing, _state.Games[Id].Status);

        Start();
        Stop();
        Assert.Equal($"status:{Id}", _p.Calls[^1]);
        _c.MarkAs(Id, PlayedStatus.Completed, false, null, null);
        Assert.Equal(new LogRequest(Id, PlayedStatus.Completed, false, null, null, null, Today), _o.Opened[^1]);
    }

    [Fact]
    public void Saved_fallback_keeps_a_newly_logged_game()
    {
        Start();
        _c.Answer(Id, PromptAnswer.Yes, Today);
        Stop();
        _c.Answer(Id, PromptAnswer.OpenLog, Today);
        var request = _o.Opened[^1];
        _c.SavedFallback(Id, request);
        Assert.Equal(GameStatus.Playing, _state.Games[Id].Status);
        Assert.Null(_state.Games[Id].PendingStart);
    }

    [Fact]
    public void MarkAs_enqueues_request()
    {
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "FEZ", Status = GameStatus.Playing };
        _c.MarkAs(Id, PlayedStatus.Retired, false, true, false);
        Assert.Equal(new LogRequest(Id, PlayedStatus.Retired, false, true, false, null, Today), _o.Opened[^1]);
    }

    [Fact]
    public void Saved_with_read_back_state_updates_record()
    {
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "FEZ", Status = GameStatus.Playing };
        _c.Saved(Id, "shelved", false);
        Assert.Equal(GameStatus.Dropped, _state.Games[Id].Status);
    }

    [Fact]
    public void Paused_shows_nothing()
    {
        _state.PromptsPaused = true;
        Start();
        Stop();
        Assert.Empty(_p.Calls);
        Assert.Empty(_o.Opened);
    }

    [Fact]
    public void Never_silences_game()
    {
        Start();
        _c.Answer(Id, PromptAnswer.Never, Today);
        Stop();
        Start();
        Assert.Equal(new[] { $"start:{Id}" }, _p.Calls);
    }

    [Fact]
    public void Answer_for_unknown_game_is_ignored()
    {
        _c.Answer("steam:9", PromptAnswer.OpenLog, Today);
        Assert.Empty(_o.Opened);
    }

    [Fact]
    public void Queued_game_is_asked_on_every_close_regardless_of_Asked()
    {
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "FEZ", PendingStart = Today, Asked = true };
        Stop();
        Stop();
        Assert.Equal(new[] { $"status:{Id}", $"status:{Id}" }, _p.Calls);
        Assert.Equal(Today, _state.Games[Id].PendingStart);
    }

    [Fact]
    public void Skip_keeps_a_queued_game_queued_and_it_is_asked_again()
    {
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "FEZ", PendingStart = Today };
        Stop();
        _c.Answer(Id, PromptAnswer.Skip, Today);
        Stop();
        Assert.Equal(Today, _state.Games[Id].PendingStart);
        Assert.Equal(new[] { $"status:{Id}", $"status:{Id}" }, _p.Calls);
    }

    [Fact]
    public void Queued_game_stays_queued_and_silent_with_ask_on_close_off()
    {
        _state.Settings = new AppSettings { AskOnClose = false };
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "FEZ", PendingStart = Today };
        Stop();
        Assert.Empty(_p.Calls);
        Assert.Equal(Today, _state.Games[Id].PendingStart);
    }

    [Fact]
    public void Never_clears_a_queued_game_and_it_is_not_asked_again()
    {
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "FEZ", PendingStart = Today };
        Stop();
        _c.Answer(Id, PromptAnswer.Never, Today);
        _p.Calls.Clear();
        Stop();
        Assert.Null(_state.Games[Id].PendingStart);
        Assert.Empty(_p.Calls);
    }

    [Fact]
    public void A_session_that_rounds_to_zero_minutes_adds_no_history_entry()
    {
        _c.Handle(new GameStopped(Id, T0, TimeSpan.FromSeconds(29)), _ => "FEZ");
        Assert.Empty(_state.Games[Id].Sessions);
        Assert.Equal(0, _state.Games[Id].LastSessionMinutes);
    }

    [Fact]
    public void Last_session_and_history_use_the_same_rounding()
    {
        _c.Handle(new GameStopped(Id, T0, TimeSpan.FromSeconds(134 * 60 + 40)), _ => "FEZ");
        var g = _state.Games[Id];
        Assert.Equal(135, g.LastSessionMinutes);
        Assert.Equal(135, g.TotalMinutes);
    }

    [Fact]
    public void Changed_is_raised_when_a_session_ends_and_when_a_log_is_saved()
    {
        var changed = new List<string>();
        _c.Changed += changed.Add;
        Start();
        Assert.Empty(changed.Where(c => c != Id).ToList());
        changed.Clear();
        Stop();
        Assert.Contains(Id, changed);
        changed.Clear();
        _c.Saved(Id, "completed", false);
        Assert.Equal(new[] { Id }, changed);
        changed.Clear();
        _c.SavedFallback(Id, LogRequest.OpenOnly(Id));
        Assert.Equal(new[] { Id }, changed);
        changed.Clear();
        _c.Answer(Id, PromptAnswer.Never, Today);
        Assert.Equal(new[] { Id }, changed);
    }

    [Fact]
    public void Changed_is_raised_after_the_question_and_the_log_request()
    {
        var seen = new List<string>();
        _c.Changed += _ => seen.Add($"changed after {_p.Calls.Count} asks, {_o.Opened.Count} opens");
        Stop();
        Assert.Equal(new[] { $"afterclose:{Id}" }, _p.Calls);
        Assert.Equal("changed after 1 asks, 0 opens", seen.Last());
        seen.Clear();
        _c.Answer(Id, PromptAnswer.AddPlaying, Today);
        Assert.Equal("changed after 1 asks, 1 opens", Assert.Single(seen));
    }

    [Fact]
    public void A_throwing_Changed_handler_loses_no_question_or_log_request()
    {
        var later = new List<string>();
        _c.Changed += _ => throw new InvalidOperationException("refill");
        _c.Changed += later.Add;
        Stop();
        Assert.Equal(new[] { $"afterclose:{Id}" }, _p.Calls);
        Assert.True(_state.Games[Id].Asked);
        _c.Answer(Id, PromptAnswer.AddPlaying, Today);
        Assert.Single(_o.Opened);
        _c.Saved(Id, "completed", false);
        Assert.Equal(new[] { Id, Id, Id }, later);
    }

    [Fact]
    public void A_new_game_raises_Changed_on_start_even_when_nothing_is_asked()
    {
        _state.Settings = _state.Settings with { AskOnStart = false };
        var changed = new List<string>();
        _c.Changed += changed.Add;
        Start();
        Assert.Empty(_p.Calls);
        Assert.Equal(new[] { Id }, changed);
    }

    [Fact]
    public void A_placeholder_name_replaced_on_start_raises_Changed_once()
    {
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "Steam app 224760", Asked = true };
        var changed = new List<string>();
        _c.Changed += changed.Add;
        Start();
        Assert.Equal("FEZ", _state.Games[Id].DisplayName);
        Assert.Equal(new[] { Id }, changed);
    }

    [Fact]
    public void A_start_that_changes_nothing_raises_nothing()
    {
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "FEZ", Asked = true };
        var changed = new List<string>();
        _c.Changed += changed.Add;
        Start();
        Assert.Empty(changed);
    }

    [Fact]
    public void Yes_then_Never_then_close_does_nothing()
    {
        Start();
        _c.Answer(Id, PromptAnswer.Yes, Today);
        _c.Answer(Id, PromptAnswer.Never, Today);
        Stop();
        Assert.Null(_state.Games[Id].PendingStart);
        Assert.Empty(_o.Opened);
        Assert.Equal(new[] { $"start:{Id}" }, _p.Calls);
    }

    [Fact]
    public void Never_from_status_popup_on_playing_game_silences_everything()
    {
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "FEZ", Status = GameStatus.Playing };
        Start();
        Stop();
        Assert.Equal(new[] { $"status:{Id}" }, _p.Calls);
        _c.Answer(Id, PromptAnswer.Never, Today);
        Assert.Equal(GameStatus.Never, _state.Games[Id].Status);
        Assert.Empty(_o.Opened);

        _p.Calls.Clear();
        Start();
        Stop();
        Assert.Empty(_p.Calls);
        Assert.Empty(_o.Opened);
    }

    [Fact]
    public void Stale_pending_on_Never_record_does_nothing_on_close()
    {
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "FEZ", Status = GameStatus.Never, PendingStart = Today };
        Stop();
        Assert.Empty(_o.Opened);
        Assert.Empty(_p.Calls);
    }

    [Fact]
    public void Placeholder_name_is_refreshed_and_slug_kept()
    {
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "Steam app 224760", Slug = "fez" };
        var before = _saves;
        _c.Handle(new GameStarted(Id, T0), _ => "FEZ");
        Assert.Equal("FEZ", _state.Games[Id].DisplayName);
        Assert.Equal("fez", _state.Games[Id].Slug);
        Assert.True(_saves > before);
    }

    [Fact]
    public void Placeholder_name_not_replaced_by_placeholder()
    {
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "Steam app 224760", Asked = true };
        var before = _saves;
        _c.Handle(new GameStarted(Id, T0), _ => "Steam app 224760");
        Assert.Equal("Steam app 224760", _state.Games[Id].DisplayName);
        Assert.Equal(before, _saves);
    }

    [Fact]
    public void Start_always_asks_even_while_a_game_loads()
    {
        Start();
        Assert.Equal(new[] { $"start:{Id}" }, _p.Calls);
        Assert.True(_state.Games[Id].Asked);
    }

    [Fact]
    public void Start_popup_that_times_out_unseen_is_asked_once_after_close()
    {
        Start();
        _c.Answer(Id, PromptAnswer.Unseen, Today);
        Stop();
        Assert.Equal(new[] { $"start:{Id}", $"afterclose:{Id}" }, _p.Calls);
        Assert.True(_state.Games[Id].Asked);
        Start();
        Stop();
        Assert.Equal(new[] { $"start:{Id}", $"afterclose:{Id}" }, _p.Calls);
    }

    [Fact]
    public void Stopped_session_is_added_to_the_play_history_on_its_local_start_date()
    {
        Start();
        Stop();
        _c.Handle(new GameStopped(Id, T0.AddHours(3), TimeSpan.FromMinutes(26)), _ => "FEZ");
        _c.Handle(new GameStopped(Id, T0.AddDays(1), TimeSpan.FromSeconds(90)), _ => "FEZ");
        var g = _state.Games[Id];
        Assert.Equal(new[] { (Today, 160), (Today.AddDays(1), 2) }, g.Sessions.Select(s => (s.Date, s.Minutes)));
        Assert.Equal(162, g.TotalMinutes);
    }

    [Fact]
    public void Session_after_midnight_counts_for_the_start_day()
    {
        var late = new DateTime(2026, 11, 8, 23, 50, 0);
        _c.Handle(new GameStopped(Id, late, TimeSpan.FromMinutes(40)), _ => "FEZ");
        Assert.Equal(new DateOnly(2026, 11, 8), _state.Games[Id].Sessions.Single().Date);
    }

    [Fact]
    public void Never_games_still_record_play_time()
    {
        Start();
        _c.Answer(Id, PromptAnswer.Never, Today);
        Stop();
        Assert.Equal(134, _state.Games[Id].TotalMinutes);
    }

    [Fact]
    public void Start_asking_off_shows_no_start_popup_and_asks_on_close()
    {
        _state.Settings = _state.Settings with { AskOnStart = false };
        Start();
        Assert.Empty(_p.Calls);
        Assert.False(_state.Games[Id].Asked);
        Stop();
        Assert.Equal(new[] { $"afterclose:{Id}" }, _p.Calls);
    }

    [Fact]
    public void Close_asking_off_shows_no_close_popup()
    {
        _state.Settings = _state.Settings with { AskOnClose = false };
        Start();
        _c.Answer(Id, PromptAnswer.Yes, Today);
        Stop();
        Assert.Equal(new[] { $"start:{Id}" }, _p.Calls);
        Assert.Empty(_o.Opened);
        Assert.Equal(Today, _state.Games[Id].PendingStart);
        Assert.Equal(134, _state.Games[Id].TotalMinutes);
    }

    [Fact]
    public void Close_asking_off_keeps_an_unknown_game_unasked()
    {
        _state.Settings = _state.Settings with { AskOnStart = false, AskOnClose = false };
        Start();
        Stop();
        Assert.Empty(_p.Calls);
        Assert.False(_state.Games[Id].Asked);
    }

    const string DolphinExe = @"c:\emulators\dolphin\dolphin.exe";

    GameRecord TrackDolphin()
    {
        var g = new GameRecord { Id = GameIds.Exe(DolphinExe), DisplayName = "Dolphin", ExePath = DolphinExe };
        _state.Games[g.Id] = g;
        return g;
    }

    [Fact]
    public void An_emulator_record_never_prompts_or_collects_play_time()
    {
        var dolphin = TrackDolphin();
        _c.Handle(new GameStarted(dolphin.Id, T0), _ => "Dolphin");
        _c.Handle(new GameStopped(dolphin.Id, T0, TimeSpan.FromMinutes(90)), _ => "Dolphin");
        Assert.Empty(_p.Calls);
        Assert.Empty(_o.Opened);
        Assert.Equal(0, dolphin.TotalMinutes);
        Assert.False(dolphin.Asked);
    }

    [Fact]
    public void An_emulator_game_is_asked_like_a_steam_game_and_keeps_its_play_time()
    {
        TrackDolphin();
        var id = GameIds.Emulator(DolphinExe, "Super Mario Sunshine");
        _c.Handle(new GameStarted(id, T0), _ => "Super Mario Sunshine");
        var g = _state.Games[id];
        Assert.Equal("Super Mario Sunshine", g.DisplayName);
        Assert.Equal(DolphinExe, g.EmulatorExe);
        Assert.Null(g.ExePath);
        Assert.False(g.IsEmulator);
        Assert.Equal(new[] { $"start:{id}" }, _p.Calls);
        _c.Handle(new GameStopped(id, T0, TimeSpan.FromMinutes(40)), _ => "ignored");
        _c.Handle(new GameStarted(id, T0.AddDays(1)), _ => "ignored");
        _c.Handle(new GameStopped(id, T0.AddDays(1), TimeSpan.FromMinutes(20)), _ => "ignored");
        Assert.Equal(60, g.TotalMinutes);
        Assert.Equal(new[] { $"start:{id}" }, _p.Calls);
    }

    [Fact]
    public void An_exe_turned_into_one_game_again_is_asked_normally()
    {
        var dolphin = TrackDolphin();
        EmulatorTitles.SetEmulator(dolphin, false);
        _c.Handle(new GameStarted(dolphin.Id, T0), _ => "Dolphin");
        Assert.Equal(new[] { $"start:{dolphin.Id}" }, _p.Calls);
    }

    [Fact]
    public void A_game_seen_for_the_first_time_is_added_now()
    {
        Start();
        Assert.Equal(Now, _state.Games[Id].AddedAt);
    }

    [Fact]
    public void A_detected_emulator_is_added_now()
    {
        _c.DetectEmulators(new[] { DuckExe });
        Assert.Equal(Now, _state.Games[GameIds.Exe(DuckExe)].AddedAt);
    }

    [Fact]
    public void A_known_game_keeps_its_added_date()
    {
        var added = new DateTime(2025, 1, 2);
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "FEZ", AddedAt = added };
        Start();
        Assert.Equal(added, _state.Games[Id].AddedAt);
    }

    [Fact]
    public void Play_is_queued_for_the_journal_while_the_switch_is_on()
    {
        var due = new List<string>();
        _c.JournalDue += due.Add;
        Stop();
        var day = Assert.Single(_state.Games[Id].PendingJournal);
        Assert.Equal(134, day.PendingMinutes);
        Assert.Equal(new[] { Id }, due);
    }

    [Fact]
    public void Play_while_the_switch_is_off_is_never_sent_later()
    {
        _state.Settings = _state.Settings with { AutoAddJournalTime = false };
        Stop();
        Assert.Empty(_state.Games[Id].PendingJournal);
        _state.Settings = _state.Settings with { AutoAddJournalTime = true };
        Assert.Empty(_state.Games[Id].PendingJournal);
    }

    [Fact]
    public void A_save_from_the_log_window_makes_the_journal_due()
    {
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "FEZ", Slug = "fez" };
        var due = new List<string>();
        _c.JournalDue += due.Add;
        _c.Saved(Id, null, playingOn: true);
        _c.SavedFallback(Id, LogRequest.Start(Id, Today));
        Assert.Equal(new[] { Id, Id }, due);
    }

    [Fact]
    public void A_journal_handler_that_throws_does_not_lose_the_close_question()
    {
        _state.Games[Id] = new GameRecord { Id = Id, DisplayName = "FEZ", Status = GameStatus.Playing, Slug = "fez" };
        _c.JournalDue += _ => throw new InvalidOperationException("journal");
        Stop();
        Assert.Contains($"status:{Id}", _p.Calls);
    }

    // A Mark as choice still waiting is dropped when the user says something newer

    GameRecord WithPick()
    {
        var g = new GameRecord { Id = Id, DisplayName = "FEZ", Slug = "fez", Status = GameStatus.Playing,
            PendingPick = new StatusPick(PlayedStatus.Shelved, null, null, null), PickTries = 1, PendingPickAt = Now };
        _state.Games[Id] = g;
        return g;
    }

    [Theory]
    [InlineData(PromptAnswer.Skip)]
    [InlineData(PromptAnswer.Never)]
    public void A_later_skip_or_never_drops_a_waiting_mark_as_choice(PromptAnswer answer)
    {
        var g = WithPick();
        _c.Answer(Id, answer, Today);
        Assert.Equal(((StatusPick?)null, 0, false), (g.PendingPick, g.PickTries, g.PickGaveUp));
    }

    [Fact]
    public void Other_answers_keep_a_waiting_choice()
    {
        var g = WithPick();
        _c.Answer(Id, PromptAnswer.NotNow, Today);
        Assert.NotNull(g.PendingPick);
    }

    [Fact]
    public void Saving_the_log_by_hand_drops_a_waiting_choice()
    {
        var g = WithPick();
        _c.Saved(Id, "completed", false);
        Assert.Null(g.PendingPick);
        var h = WithPick();
        _c.SavedFallback(Id, new LogRequest(Id, PlayedStatus.Completed, null, null, null, null, null));
        Assert.Null(h.PendingPick);
    }
}

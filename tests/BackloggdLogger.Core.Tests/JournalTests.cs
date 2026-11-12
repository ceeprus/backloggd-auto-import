using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class JournalTests : IDisposable
{
    static readonly DateOnly D1 = new(2026, 11, 7);
    static readonly DateOnly D2 = new(2026, 11, 8);
    const string Id = "steam:1145360";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "bl-tests-" + Guid.NewGuid().ToString("N"));
    string StatePath => Path.Combine(_dir, "state.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    static GameRecord Logged(string id = Id, string slug = "hades", GameStatus status = GameStatus.Playing) =>
        new() { Id = id, DisplayName = "Hades", Slug = slug, Status = status };

    static readonly JournalGate Clear = new(GameRunning: false, ScreenBusy: false, WindowOpen: false, AppInFront: false);

    (AppState State, JournalScheduler Scheduler, Func<int> Saves) WithGame(GameRecord g)
    {
        var state = new AppState();
        state.Games[g.Id] = g;
        var saves = 0;
        return (state, new JournalScheduler(state, () => saves++), () => saves);
    }

    // Play days

    [Fact]
    public void Play_for_the_journal_is_pending_until_sent()
    {
        var g = Logged();
        g.AddPlay(D2, 40, forJournal: true);
        g.AddPlay(D2, 15, forJournal: true);
        var day = Assert.Single(g.PendingJournal);
        Assert.Equal(55, day.PendingMinutes);
        Assert.Equal(0, day.PushedMinutes);
    }

    [Fact]
    public void Play_not_for_the_journal_is_never_pending()
    {
        var g = Logged();
        g.AddPlay(D1, 30);
        g.AddPlay(D2, 30, forJournal: false);
        Assert.Empty(g.PendingJournal);
        Assert.All(g.Sessions, d => Assert.Equal(d.Minutes, d.PushedMinutes));
        Assert.Equal(0, g.SentJournalMinutes);
    }

    [Fact]
    public void Mixed_play_on_one_day_keeps_only_the_journal_part_pending()
    {
        var g = Logged();
        g.AddPlay(D2, 20);
        g.AddPlay(D2, 35, forJournal: true);
        Assert.Equal(35, Assert.Single(g.PendingJournal).PendingMinutes);
    }

    [Theory]
    [InlineData("hades", GameStatus.Playing, true)]
    [InlineData("hades", GameStatus.Finished, true)]
    [InlineData("hades", GameStatus.Dropped, true)]
    [InlineData("hades", GameStatus.Unknown, false)]
    [InlineData("hades", GameStatus.Never, false)]
    [InlineData(null, GameStatus.Playing, false)]
    [InlineData("Not A Slug", GameStatus.Playing, false)]
    [InlineData("../x", GameStatus.Playing, false)]
    public void Only_games_logged_on_backloggd_are_synced(string? slug, GameStatus status, bool eligible)
    {
        var g = new GameRecord { Id = Id, DisplayName = "Hades", Slug = slug, Status = status };
        Assert.Equal(eligible, Journal.Eligible(g));
    }

    [Fact]
    public void An_emulator_record_is_never_synced()
    {
        var g = new GameRecord { Id = "exe:c:\\d\\dolphin.exe", DisplayName = "Dolphin", ExePath = @"c:\d\dolphin.exe", Slug = "dolphin", Status = GameStatus.Playing };
        Assert.False(Journal.Eligible(g));
    }

    // Upgrade from a file written before the journal existed

    [Fact]
    public void Play_recorded_before_the_feature_is_never_sent_and_the_switch_turns_on()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """
            { "Games": { "steam:1": { "Id": "steam:1", "DisplayName": "Hades", "Slug": "hades", "Status": "Playing",
              "Sessions": [ { "Date": "2026-11-07", "Minutes": 60 }, { "Date": "2026-11-08", "Minutes": 35 } ] } },
              "Settings": { "AutoAddJournalTime": false } }
            """);
        var state = new StateStore(StatePath).Load();
        var g = state.Games["steam:1"];
        Assert.Empty(g.PendingJournal);
        Assert.Equal(new[] { 60, 35 }, g.Sessions.Select(d => d.PushedMinutes));
        Assert.Equal(0, g.SentJournalMinutes);
        Assert.True(state.Settings.AutoAddJournalTime);
        Assert.True(state.JournalBaselined);
    }

    [Fact]
    public void After_the_upgrade_new_play_stays_pending_across_loads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{ "Games": { "steam:1": { "Id": "steam:1", "Sessions": [ { "Date": "2026-11-07", "Minutes": 60 } ] } } }""");
        var store = new StateStore(StatePath);
        var state = store.Load();
        state.Games["steam:1"].AddPlay(D2, 25, forJournal: true);
        state.Settings = state.Settings with { AutoAddJournalTime = false };
        store.Save(state);

        var again = new StateStore(StatePath).Load();
        Assert.Equal(25, Assert.Single(again.Games["steam:1"].PendingJournal).PendingMinutes);
        Assert.False(again.Settings.AutoAddJournalTime);
    }

    // Retry after a build that fixes journal runs

    [Fact]
    public void Given_up_days_get_their_tries_back_once_after_an_upgrade()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """
            { "JournalBaselined": true,
              "Games": { "steam:1": { "Id": "steam:1", "DisplayName": "Hades", "Slug": "hades", "Status": "Dropped", "Sessions": [
                { "Date": "2026-11-06", "Minutes": 60, "PushedMinutes": 60, "JournalTries": 1 },
                { "Date": "2026-11-07", "Minutes": 90, "PushedMinutes": 30, "JournalTries": 3, "ToldMinutes": 60 },
                { "Date": "2026-11-08", "Minutes": 45, "JournalTries": 2, "UnsupportedOnce": true, "TriedTotal": 345, "TriedMinutes": 45 } ] } } }
            """);
        var store = new StateStore(StatePath);
        var state = store.Load();
        var d = state.Games["steam:1"].Sessions;
        Assert.Equal(Journal.FormatVersion, state.JournalVersion);
        // A day with nothing pending keeps its count; pending days start over with what was sent kept.
        Assert.Equal((60, 1), (d[0].PushedMinutes, d[0].JournalTries));
        Assert.Equal((30, 0, 60, false), (d[1].PushedMinutes, d[1].JournalTries, d[1].ToldMinutes, d[1].GaveUp));
        // A day with an unconfirmed save keeps its count: its notice asked the user to check it.
        Assert.Equal((0, 2, true, (int?)345, 45), (d[2].PushedMinutes, d[2].JournalTries, d[2].UnsupportedOnce, d[2].TriedTotal, d[2].TriedMinutes));
        Assert.Equal(2, state.Games["steam:1"].PendingJournal.Count);

        // Once: tries after the upgrade count again, and the version is stored.
        d[1].JournalTries = 3;
        store.Save(state);
        var again = new StateStore(StatePath).Load();
        Assert.Equal(3, again.Games["steam:1"].Sessions[1].JournalTries);
        Assert.Contains("\"JournalVersion\"", File.ReadAllText(StatePath));
        state.UpgradeJournal();
        Assert.Equal(3, d[1].JournalTries);
    }

    [Fact]
    public void A_given_up_day_with_an_unconfirmed_save_stays_given_up_after_an_upgrade()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """
            { "JournalBaselined": true, "JournalVersion": 1,
              "Games": { "steam:1": { "Id": "steam:1", "DisplayName": "Hades", "Slug": "hades", "Status": "Playing", "Sessions": [
                { "Date": "2026-11-07", "Minutes": 90, "JournalTries": 3, "TriedTotal": 390, "TriedMinutes": 90, "ToldMinutes": 90 },
                { "Date": "2026-11-08", "Minutes": 45, "JournalTries": 3, "ToldMinutes": 45 } ] } } }
            """);
        var d = new StateStore(StatePath).Load().Games["steam:1"].Sessions;
        Assert.Equal((3, (int?)390, true), (d[0].JournalTries, d[0].TriedTotal, d[0].GaveUp));
        Assert.Equal((0, false), (d[1].JournalTries, d[1].GaveUp));
    }

    [Fact]
    public void A_fresh_install_starts_at_the_current_journal_version()
    {
        Assert.Equal(Journal.FormatVersion, new StateStore(StatePath).Load().JournalVersion);
    }

    [Fact]
    public void A_fresh_install_is_baselined_so_its_first_play_is_kept_pending()
    {
        var store = new StateStore(StatePath);
        var state = store.Load();
        Assert.True(state.JournalBaselined);
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        state.Games[g.Id] = g;
        store.Save(state);
        Assert.Single(new StateStore(StatePath).Load().Games[g.Id].PendingJournal);
    }

    [Fact]
    public void Journal_fields_round_trip_and_computed_ones_are_not_written()
    {
        var store = new StateStore(StatePath);
        var state = store.Load();
        var g = Logged();
        g.AddPlay(D1, 50, forJournal: true);
        g.AddPlay(D2, 70, forJournal: true);
        g.Sessions[0].PushedMinutes = 50;
        g.Sessions[0].SentMinutes = 50;
        g.Sessions[1].JournalTries = 2;
        g.Sessions[1].TriedTotal = 400;
        g.Sessions[1].TriedMinutes = 70;
        state.Games[g.Id] = g;
        store.Save(state);
        var text = File.ReadAllText(StatePath);
        Assert.DoesNotContain("PendingJournal", text);
        Assert.DoesNotContain("PendingMinutes", text);
        Assert.DoesNotContain("SentJournalMinutes", text);

        var d = new StateStore(StatePath).Load().Games[g.Id].Sessions;
        Assert.Equal((50, 50, 0), (d[0].PushedMinutes, d[0].SentMinutes, d[0].JournalTries));
        Assert.Equal((0, 2, 400, 70), (d[1].PushedMinutes, d[1].JournalTries, d[1].TriedTotal, d[1].TriedMinutes));
    }

    // The scheduler

    [Fact]
    public void Nothing_runs_until_kicked()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        Assert.Null(s.Next(Clear));
        s.Kick();
        Assert.NotNull(s.Next(Clear));
    }

    [Fact]
    public void The_job_is_the_oldest_pending_day_with_its_pending_minutes()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        g.AddPlay(D1, 45, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        var job = s.Next(Clear)!;
        Assert.Equal(new JournalJob(Id, "hades", D1, 45, null), job);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void Never_runs_during_play_a_busy_screen_an_open_window_or_while_the_app_is_in_front(bool game, bool busy, bool window, bool front)
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        Assert.Null(s.Next(new JournalGate(game, busy, window, front)));
        Assert.True(s.Due);
        Assert.NotNull(s.Next(Clear));
    }

    [Fact]
    public void Never_runs_while_the_switch_is_off()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (state, s, _) = WithGame(g);
        state.Settings = state.Settings with { AutoAddJournalTime = false };
        s.Kick();
        Assert.Null(s.Next(Clear));
    }

    [Fact]
    public void A_game_not_on_backloggd_waits_until_it_is_logged()
    {
        var g = Logged(status: GameStatus.Unknown);
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        Assert.Null(s.Next(Clear));
        Assert.False(s.Due);
        g.Status = GameStatus.Playing;
        s.Kick();
        Assert.NotNull(s.Next(Clear));
    }

    [Fact]
    public void A_day_is_tried_once_per_pass()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        var job = s.Next(Clear)!;
        s.Finish(job, JournalOutcome.Failed("log box"));
        Assert.Null(s.Next(Clear));
        Assert.False(s.Due);
        s.Kick();
        Assert.Equal(job, s.Next(Clear));
    }

    [Fact]
    public void A_save_marks_the_minutes_sent()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, saves) = WithGame(g);
        s.Kick();
        var job = s.Next(Clear)!;
        var gaveUp = s.Finish(job, JournalOutcome.Saved(330));
        Assert.False(gaveUp);
        Assert.Empty(g.PendingJournal);
        Assert.Equal(30, g.Sessions[0].PushedMinutes);
        Assert.Equal(30, g.SentJournalMinutes);
        Assert.True(saves() > 0);
    }

    [Fact]
    public void Play_added_while_a_run_was_out_stays_pending()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        var job = s.Next(Clear)!;
        g.AddPlay(D2, 10, forJournal: true);
        s.Finish(job, JournalOutcome.Saved(330));
        Assert.Equal(10, Assert.Single(g.PendingJournal).PendingMinutes);
        Assert.Equal(30, g.SentJournalMinutes);
    }

    [Fact]
    public void Failures_never_mark_minutes_and_the_third_one_gives_up_once()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        var results = new List<bool>();
        for (var i = 0; i < 3; i++)
        {
            s.Kick();
            var job = s.Next(Clear)!;
            results.Add(s.Finish(job, JournalOutcome.Failed("calendar")));
            Assert.Equal(0, g.Sessions[0].PushedMinutes);
        }
        Assert.Equal(new[] { false, false, true }, results);
        s.Kick();
        Assert.Null(s.Next(Clear));
        Assert.Empty(g.PendingJournal);
        Assert.Equal(0, g.SentJournalMinutes);
        Assert.Equal(30, g.Sessions[0].Minutes - g.Sessions[0].PushedMinutes);
    }

    [Fact]
    public void An_unsupported_day_is_tried_again_and_gives_up_only_when_a_later_run_confirms_it()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        Assert.False(s.Finish(s.Next(Clear)!, JournalOutcome.Unsupported("new session")));
        Assert.Single(g.PendingJournal);
        Assert.Null(s.Next(Clear));
        s.Kick();
        Assert.True(s.Finish(s.Next(Clear)!, JournalOutcome.Unsupported("new session")));
        Assert.Equal(0, g.Sessions[0].PushedMinutes);
        s.Kick();
        Assert.Null(s.Next(Clear));
    }

    [Fact]
    public void A_save_after_a_first_unsupported_run_clears_it()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        s.Finish(s.Next(Clear)!, JournalOutcome.Unsupported("new session"));
        s.Kick();
        s.Finish(s.Next(Clear)!, JournalOutcome.Saved(30));
        Assert.False(g.Sessions[0].UnsupportedOnce);
        Assert.Equal(0, g.Sessions[0].JournalTries);
    }

    [Fact]
    public void A_stopped_run_is_not_a_failure_and_is_tried_again_in_the_same_pass()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        var job = s.Next(Clear)!;
        Assert.False(s.Finish(job, JournalOutcome.Stopped));
        Assert.Equal(0, g.Sessions[0].JournalTries);
        Assert.Equal(job, s.Next(Clear));
    }

    [Fact]
    public void A_pass_stops_offering_a_day_after_three_stopped_runs()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        for (var i = 0; i < Journal.MaxStopsPerPass; i++)
            s.Finish(s.Next(Clear)!, JournalOutcome.Stopped);
        Assert.Null(s.Next(Clear));
        Assert.Equal(0, g.Sessions[0].JournalTries);
        s.Kick();
        Assert.NotNull(s.Next(Clear));
    }

    [Fact]
    public void The_total_is_written_down_before_save_is_pressed()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (state, s, saves) = WithGame(g);
        s.Kick();
        var job = s.Next(Clear)!;
        var before = saves();
        Assert.True(s.Prepare(job, 330));
        Assert.True(saves() > before);
        Assert.Equal(((int?)330, 30), (g.Sessions[0].TriedTotal, g.Sessions[0].TriedMinutes));
        state.Games.Clear();
        Assert.False(s.Prepare(job, 330));
    }

    [Fact]
    public void A_run_that_never_pressed_save_puts_the_day_back_as_it_was()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        var job = s.Next(Clear)!;
        s.Prepare(job, 330);
        s.Finish(job, JournalOutcome.Failed("guard"));
        Assert.Equal(((int?)null, 0, 1), (g.Sessions[0].TriedTotal, g.Sessions[0].TriedMinutes, g.Sessions[0].JournalTries));
    }

    [Fact]
    public void A_run_that_never_pressed_save_keeps_an_earlier_unconfirmed_save()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        s.Finish(s.Next(Clear)!, JournalOutcome.Failed("save response", target: 330));
        s.Kick();
        var job = s.Next(Clear)!;
        Assert.Equal(((int?)330, 30), (job.Already, job.AlreadyMinutes));
        s.Prepare(job, 400);
        s.Finish(job, JournalOutcome.Stopped);
        Assert.Equal(((int?)330, 30), (g.Sessions[0].TriedTotal, g.Sessions[0].TriedMinutes));
    }

    [Theory]
    [InlineData(JournalOutcomeKind.Stopped)]
    [InlineData(JournalOutcomeKind.Failed)]
    [InlineData(JournalOutcomeKind.Unsupported)]
    [InlineData(JournalOutcomeKind.Already)]
    public void After_save_is_pressed_every_outcome_but_saved_carries_the_target(JournalOutcomeKind kind)
    {
        var run = new JournalRun();
        run.Pressing(330);
        var outcome = run.Close(new JournalOutcome(kind, kind == JournalOutcomeKind.Failed ? "save response" : null));
        Assert.Equal(JournalOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(330, outcome.Target);
        Assert.False(string.IsNullOrEmpty(outcome.Step));
        if (kind == JournalOutcomeKind.Failed) Assert.Equal("save response", outcome.Step);
    }

    [Fact]
    public void After_save_is_pressed_a_confirmed_save_stays_saved_and_activation_is_kept()
    {
        var run = new JournalRun();
        run.Pressing(330);
        Assert.Equal(JournalOutcome.Saved(330), run.Close(JournalOutcome.Saved(330)));
        Assert.True(run.Close(JournalOutcome.Failed("activated") with { Activated = true }).Activated);
    }

    [Fact]
    public void A_save_the_page_refused_to_press_carries_no_target()
    {
        var run = new JournalRun();
        run.Pressing(330);
        run.NotPressed();
        Assert.Equal(JournalOutcome.Failed("guard"), run.Close(JournalOutcome.Failed("guard")));
        Assert.Equal(JournalOutcome.Stopped, new JournalRun().Close(JournalOutcome.Stopped));
    }

    [Fact]
    public void A_pressed_save_that_failed_is_checked_by_the_next_run()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        var job = s.Next(Clear)!;
        s.Prepare(job, 330);
        var run = new JournalRun();
        run.Pressing(330);
        s.Finish(job, run.Close(JournalOutcome.Stopped));
        s.Kick();
        Assert.Equal(330, s.Next(Clear)!.Already);
    }

    [Fact]
    public void A_kick_during_a_run_waits_for_its_end()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        var job = s.Next(Clear)!;
        s.Kick();
        Assert.Null(s.Next(Clear));
        s.Finish(job, JournalOutcome.Stopped);
        var again = s.Next(Clear)!;
        Assert.Equal(job, again);
        s.Kick();
        s.Finish(again, JournalOutcome.Failed("calendar"));
        Assert.True(s.Due);
        Assert.Equal(job with { Already = null }, s.Next(Clear));
    }

    [Fact]
    public void Stopped_runs_stay_capped_even_when_kicked_during_them()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        for (var i = 0; i < Journal.MaxStopsPerPass; i++)
        {
            var job = s.Next(Clear)!;
            s.Kick();
            s.Finish(job, JournalOutcome.Stopped);
        }
        Assert.True(s.Due);
        Assert.Null(s.Next(Clear));
        s.Kick();
        Assert.NotNull(s.Next(Clear));
    }

    [Fact]
    public void The_job_carries_the_game_status_the_log_box_must_fit()
    {
        var g = Logged(status: GameStatus.Finished);
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        Assert.Equal(GameStatus.Finished, s.Next(Clear)!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_is_not_allowed_when_the_total_could_not_be_written(bool throws)
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var state = new AppState();
        state.Games[g.Id] = g;
        var s = new JournalScheduler(state, () => { }, () => throws ? throw new IOException("disk") : false);
        s.Kick();
        var job = s.Next(Clear)!;
        Assert.False(s.Prepare(job, 330));
        s.Finish(job, JournalOutcome.Failed("state not written"));
        Assert.Null(g.Sessions[0].TriedTotal);
        var ok = new JournalScheduler(state, () => { }, () => true);
        ok.Kick();
        Assert.True(ok.Prepare(ok.Next(Clear)!, 330));
    }

    [Fact]
    public void A_run_that_ends_while_the_commit_is_unanswered_keeps_the_target()
    {
        var run = new JournalRun();
        run.Committing(330);
        Assert.True(run.IsCommitting);
        foreach (var o in new[] { JournalOutcome.Stopped, JournalOutcome.Failed("timeout"), JournalOutcome.Failed("browser process"), JournalOutcome.Failed("activated") })
            Assert.Equal(330, run.Close(o).Target);
    }

    [Fact]
    public void A_commit_answer_decides_the_press()
    {
        var refused = new JournalRun();
        refused.Committing(330);
        refused.Answered(false);
        Assert.False(refused.IsCommitting);
        Assert.Equal(JournalOutcome.Failed("guard"), refused.Close(JournalOutcome.Failed("guard")));

        var pressed = new JournalRun();
        pressed.Committing(330);
        pressed.Answered(true);
        Assert.Equal(330, pressed.PressedTarget);
        Assert.Equal(330, pressed.Close(JournalOutcome.Failed("save response")).Target);
    }

    [Fact]
    public void A_sent_save_keeps_the_target_and_a_refused_one_drops_it()
    {
        var run = new JournalRun();
        run.Committing(330);
        Assert.Null(run.Close(JournalOutcome.Failed("request dates: time"), refused: true).Target);
        var sent = new JournalRun();
        Assert.Equal(330, sent.Close(JournalOutcome.Stopped, sentTarget: 330).Target);
        Assert.Equal(JournalOutcome.Saved(330), sent.Close(JournalOutcome.Saved(330), sentTarget: 330));
    }

    [Fact]
    public async Task A_commit_that_doesnt_answer_in_time_counts_as_pressed()
    {
        var never = new TaskCompletionSource<string>();
        Assert.Equal(new JournalCommitResult(true, "commit timeout"), await JournalCommitResult.RunAsync(() => never.Task, TimeSpan.FromMilliseconds(50)));
        Assert.Equal(new JournalCommitResult(true, "commit"), await JournalCommitResult.RunAsync(() => throw new InvalidOperationException("gone"), TimeSpan.FromSeconds(1)));
        Assert.Equal(new JournalCommitResult(true, "commit"), await JournalCommitResult.RunAsync(() => Task.FromException<string>(new InvalidOperationException("gone")), TimeSpan.FromSeconds(1)));
        Assert.Equal(new JournalCommitResult(true, "save"), await JournalCommitResult.RunAsync(() => Task.FromResult("""{"pressed":true}"""), TimeSpan.FromSeconds(1)));
        Assert.Equal(new JournalCommitResult(false, "guard"), await JournalCommitResult.RunAsync(() => Task.FromResult("""{"pressed":false,"step":"guard"}"""), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_run_takes_one_script_message()
    {
        var run = new JournalRun();
        Assert.True(run.TakeMessage());
        Assert.False(run.TakeMessage());
        Assert.False(run.TakeMessage());
    }

    // The session an unconfirmed save was for

    [Fact]
    public void An_unconfirmed_save_remembers_its_session_and_the_rerun_carries_it()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        var job = s.Next(Clear)!;
        s.Prepare(job, 330, "17557166");
        s.Finish(job, JournalOutcome.Failed("save response", 330));
        Assert.Equal("17557166", g.Sessions[0].TriedSession);
        s.Kick();
        var rerun = s.Next(Clear)!;
        Assert.Equal((330, "17557166"), (rerun.Already, rerun.AlreadySession));
        Assert.False(rerun.TriedSessionChanged("17557166"));
        Assert.True(rerun.TriedSessionChanged("17557200"));
        Assert.True(rerun.TriedSessionChanged(null));
        // Confirmed: forgotten with the total.
        s.Finish(rerun, JournalOutcome.Already);
        Assert.Null(g.Sessions[0].TriedSession);
    }

    [Fact]
    public void A_made_session_is_remembered_as_new_and_matches_whatever_id_it_got()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        var job = s.Next(Clear)!;
        s.Prepare(job, 30, "-2");
        Assert.Equal("new", g.Sessions[0].TriedSession);
        s.Finish(job, JournalOutcome.Stopped);
        Assert.Null(g.Sessions[0].TriedSession);
        s.Prepare(s.Next(Clear)!, 30, "-2");
        var rerun = new JournalJob(Id, "hades", D2, 30, 30, 30, AlreadySession: "new");
        Assert.False(rerun.TriedSessionChanged("17557300"));
        Assert.False(new JournalJob(Id, "hades", D2, 30, null).TriedSessionChanged("1"));
    }

    [Fact]
    public void A_run_that_never_pressed_save_keeps_the_earlier_tried_session()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        var first = s.Next(Clear)!;
        s.Prepare(first, 330, "77");
        s.Finish(first, JournalOutcome.Failed("save response", 330));
        s.Kick();
        var second = s.Next(Clear)!;
        s.Prepare(second, 400, "78");
        s.Finish(second, JournalOutcome.Failed("tried session changed"));
        Assert.Equal(((int?)330, "77"), (g.Sessions[0].TriedTotal, g.Sessions[0].TriedSession));
    }

    // One unconfirmed day at a time

    [Fact]
    public void While_a_day_waits_for_its_save_to_be_confirmed_only_that_day_runs_for_the_game()
    {
        var g = Logged();
        g.AddPlay(D1, 60, forJournal: true);
        g.AddPlay(D2, 30, forJournal: true);
        var other = Logged(id: "steam:2", slug: "other");
        other.AddPlay(D2, 15, forJournal: true);
        var (state, s, _) = WithGame(g);
        state.Games[other.Id] = other;
        s.Kick();
        var first = s.Next(Clear)!;
        Assert.Equal(D1, first.Date);
        s.Finish(first, JournalOutcome.Failed("save response", 400));
        // The same pass: D2 of this game waits; the other game goes on.
        var next = s.Next(Clear)!;
        Assert.Equal((other.Id, D2), (next.GameId, next.Date));
        s.Finish(next, JournalOutcome.Saved(15));
        Assert.Null(s.Next(Clear));
        // Next pass: only the unconfirmed day, until it is settled.
        s.Kick();
        var again = s.Next(Clear)!;
        Assert.Equal((D1, (int?)400), (again.Date, again.Already));
        s.Finish(again, JournalOutcome.Already);
        Assert.Equal(D2, s.Next(Clear)!.Date);
    }

    // Time typed in by hand after a notice

    [Theory]
    [InlineData(88, 88, 88, false, true, 0)]     // told 88, the session holds 88: there, nothing played since
    [InlineData(88, 108, 88, false, true, 20)]   // 20 played since the notice: only those go in
    [InlineData(88, 108, 150, false, true, 20)]  // the session holds more than told: still only the 20
    [InlineData(88, 88, 87, false, false, 88)]   // one short of the told time: adds all pending
    [InlineData(88, 88, 30, false, false, 88)]   // the told time isn't there: adds all pending
    [InlineData(0, 88, 88, false, false, 88)]    // never told: adds onto the session as always
    [InlineData(88, 88, 0, true, false, 88)]     // the day had no session: a new one gets it all
    public void After_a_notice_the_told_part_is_checked_against_the_session(int told, int add, int session, bool newSession, bool there, int adds)
    {
        var job = new JournalJob(Id, "hades", D2, add, null, Told: told);
        Assert.Equal(there, job.ToldThere(session, newSession));
        Assert.Equal(adds, job.AddFor(session, newSession));
    }

    [Fact]
    public void A_told_day_whose_session_holds_it_is_marked_handled_without_saving()
    {
        var g = Logged();
        g.AddPlay(D2, 88, forJournal: true);
        g.Sessions[0].JournalTries = Journal.MaxTries;
        var (_, s, _) = WithGame(g);
        s.TakeNotices();
        Assert.Equal(88, g.Sessions[0].ToldMinutes);
        g.Sessions[0].JournalTries = 0;
        s.Kick();
        var job = s.Next(Clear)!;
        Assert.Equal(88, job.Told);
        Assert.Equal(0, job.AddFor(88, false));
        Assert.False(s.Finish(job, JournalOutcome.Covered(88, 88)));
        Assert.Empty(g.PendingJournal);
        Assert.Equal((88, 0, 0), (g.Sessions[0].PushedMinutes, g.Sessions[0].ToldMinutes, g.Sessions[0].JournalTries));
        // Typed in by the user, not sent by the app.
        Assert.Equal(0, g.SentJournalMinutes);
        Assert.Equal("already there after a notice (session 88m, told 88m)", JournalOutcome.Covered(88, 88).Step);

        // Later play that day is an ordinary job again.
        g.AddPlay(D2, 20, forJournal: true);
        var next = s.Next(Clear)!;
        Assert.Equal((20, 0), (next.Add, next.Told));
        Assert.False(next.ToldThere(108, false));
    }

    [Fact]
    public void Play_since_the_notice_is_added_and_only_that_counts_as_sent_by_the_app()
    {
        var g = Logged();
        g.AddPlay(D2, 88, forJournal: true);
        g.Sessions[0].ToldMinutes = 88;
        g.AddPlay(D2, 20, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        var job = s.Next(Clear)!;
        Assert.Equal((108, 88), (job.Add, job.Told));
        Assert.Equal(20, job.AddFor(88, false));
        s.Finish(job, JournalOutcome.Saved(108) with { ToldThere = true });
        Assert.Equal((108, 0, 20), (g.Sessions[0].PushedMinutes, g.Sessions[0].ToldMinutes, g.SentJournalMinutes));
    }

    [Fact]
    public void A_saved_run_clears_the_told_minutes()
    {
        var g = Logged();
        g.AddPlay(D2, 88, forJournal: true);
        g.Sessions[0].ToldMinutes = 88;
        var (_, s, _) = WithGame(g);
        s.Kick();
        var job = s.Next(Clear)!;
        Assert.False(job.ToldThere(30, false));
        s.Finish(job, JournalOutcome.Saved(118));
        Assert.Equal((88, 0, 88), (g.Sessions[0].PushedMinutes, g.Sessions[0].ToldMinutes, g.SentJournalMinutes));
    }

    // Notices

    [Fact]
    public void Given_up_days_are_told_once_per_game_and_again_after_more_play()
    {
        var g = Logged();
        g.AddPlay(D1, 30, forJournal: true);
        g.AddPlay(D2, 45, forJournal: true);
        var (_, s, _) = WithGame(g);
        Assert.Empty(s.TakeNotices());
        foreach (var d in g.Sessions) d.JournalTries = Journal.MaxTries;
        var n = Assert.Single(s.TakeNotices());
        Assert.Equal(new[] { D1, D2 }, n.Days.Select(d => d.Date));
        Assert.Empty(s.TakeNotices());
        g.AddPlay(D2, 10, forJournal: true);
        var again = Assert.Single(s.TakeNotices());
        Assert.Equal(55, Assert.Single(again.Days).PendingMinutes);
        Assert.Empty(s.TakeNotices());
    }

    [Fact]
    public void Notice_body_lists_each_day_or_sums_many()
    {
        var today = new DateTime(2026, 11, 9);
        Assert.Equal($"Add {Format.Minutes(95)} for Nov 8 in the log.", Journal.NoticeBody([(D2, 95, false)], today));
        Assert.Equal($"Add 30m for Nov 7 and {Format.Minutes(95)} for Nov 8 in the log.", Journal.NoticeBody([(D2, 95, false), (D1, 30, false)], today));
        var many = new[] { 1, 2, 3, 4 }.Select(i => (new DateOnly(2026, 11, i), 30, false)).ToList();
        Assert.Equal($"Add {Format.Minutes(120)} over 4 days, Nov 1 to Nov 4, in the log.", Journal.NoticeBody(many, today));
    }

    [Fact]
    public void Notice_body_says_check_when_a_save_may_have_landed()
    {
        var today = new DateTime(2026, 11, 9);
        Assert.Equal($"Check the journal for Nov 8: {Format.Minutes(95)} may already be there.", Journal.NoticeBody([(D2, 95, true)], today));
        Assert.Equal($"Check the journal for Nov 7 and Nov 8: {Format.Minutes(125)} may already be there.", Journal.NoticeBody([(D1, 30, false), (D2, 95, true)], today));
    }

    [Fact]
    public void A_save_that_was_sent_but_not_confirmed_is_checked_before_adding_again()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        var first = s.Next(Clear)!;
        s.Finish(first, JournalOutcome.Failed("save response", target: 330));
        Assert.Equal(0, g.Sessions[0].PushedMinutes);

        s.Kick();
        var retry = s.Next(Clear)!;
        Assert.Equal(330, retry.Already);
        Assert.Equal(30, retry.Add);
        s.Finish(retry, JournalOutcome.Already);
        Assert.Empty(g.PendingJournal);
        Assert.Equal(30, g.SentJournalMinutes);
        Assert.Null(g.Sessions[0].TriedTotal);
    }

    [Fact]
    public void After_an_unconfirmed_save_more_play_that_day_is_still_sent()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (_, s, _) = WithGame(g);
        s.Kick();
        s.Finish(s.Next(Clear)!, JournalOutcome.Failed("save response", target: 330));
        g.AddPlay(D2, 20, forJournal: true);
        s.Kick();
        var retry = s.Next(Clear)!;
        Assert.Equal(50, retry.Add);
        s.Finish(retry, JournalOutcome.Already);
        Assert.Equal(20, Assert.Single(g.PendingJournal).PendingMinutes);
        var next = s.Next(Clear)!;
        Assert.Equal((20, (int?)null), (next.Add, next.Already));
    }

    [Fact]
    public void A_job_for_a_record_that_went_away_is_ignored()
    {
        var g = Logged();
        g.AddPlay(D2, 30, forJournal: true);
        var (state, s, _) = WithGame(g);
        s.Kick();
        var job = s.Next(Clear)!;
        state.Games.Clear();
        Assert.False(s.Finish(job, JournalOutcome.Saved(1)));
    }

    // The script's message

    [Fact]
    public void Script_message_ready()
    {
        var r = JournalScriptResult.Parse("""{"type":"journal","ok":true,"ready":true,"gameId":"123","target":330,"current":300,"log":{"status":"completed","rating":null},"sessionId":"77","sessionBoxes":[false,true]}""");
        Assert.Equal(JournalScriptKind.Ready, r.Kind);
        Assert.Equal("123", r.GameId);
        Assert.Equal((330, 300), (r.Target, r.Current));
        Assert.Equal("completed", r.Log["status"]);
        Assert.Null(r.Log["rating"]);
        Assert.Equal("77", r.SessionId);
        Assert.True(r.SessionBoxes);
        Assert.False(r.NewSession);
        var made = JournalScriptResult.Parse("""{"type":"journal","ok":true,"ready":true,"gameId":"123","target":95,"current":0,"log":{},"sessionId":"-2","sessionBoxes":[false,false],"newSession":true,"playthroughId":"901"}""");
        Assert.Equal((JournalScriptKind.Ready, true, "901", "-2"), (made.Kind, made.NewSession, made.PlaythroughId, made.SessionId));
    }

    [Fact]
    public void Script_message_already()
    {
        var r = JournalScriptResult.Parse("""{"type":"journal","ok":true,"already":true,"gameId":"123","current":330}""");
        Assert.Equal(JournalScriptKind.Already, r.Kind);
        Assert.Equal(330, r.Current);
    }

    [Fact]
    public void Script_message_unsupported()
    {
        var r = JournalScriptResult.Parse("""{"type":"journal","ok":false,"unsupported":true,"step":"new session"}""");
        Assert.Equal(JournalScriptKind.Unsupported, r.Kind);
        Assert.Equal("new session", r.Step);
    }

    [Theory]
    [InlineData("""{"type":"journal","ok":false,"step":"log box"}""", "log box")]
    [InlineData("""{"type":"journal","ok":false,"step":"script","error":"x is undefined"}""", "script")]
    [InlineData("""{"type":"journal","ok":true}""", "message")]
    [InlineData("""{"type":"journal","ok":true,"saved":true,"gameId":"1","target":3}""", "message")]
    [InlineData("""{"type":"journal","ok":true,"ready":true,"gameId":"1","target":3,"current":0}""", "message")]
    [InlineData("""{"type":"journal","ok":true,"ready":true,"target":3,"current":0,"log":{},"sessionBoxes":[false,false]}""", "message")]
    [InlineData("""{"type":"journal","ok":true,"ready":true,"gameId":"1","target":3,"current":0,"log":{}}""", "message")]
    [InlineData("""{"type":"journal","ok":true,"ready":true,"gameId":"1","target":3,"current":0,"log":{},"sessionBoxes":[false]}""", "message")]
    [InlineData("""{"type":"journal","ok":true,"ready":true,"gameId":"1","target":3,"current":0,"log":{},"sessionBoxes":[0,1]}""", "message")]
    [InlineData("""{"type":"prefill"}""", "message")]
    [InlineData("""not json""", "message")]
    [InlineData("""[1,2]""", "message")]
    public void Anything_else_is_a_failure_with_its_step(string json, string step)
    {
        var r = JournalScriptResult.Parse(json);
        Assert.Equal(JournalScriptKind.Failed, r.Kind);
        Assert.Equal(step, r.Step);
    }

    [Fact]
    public void Day_formats_like_the_added_column()
    {
        Assert.Equal("Nov 8", Format.Day(D2, new DateTime(2026, 12, 1)));
        Assert.Equal("Nov 8, 2026", Format.Day(D2, new DateTime(2027, 1, 1)));
    }
}

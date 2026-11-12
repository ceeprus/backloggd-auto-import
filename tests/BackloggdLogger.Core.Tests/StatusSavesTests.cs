using System.Text;
using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class StatusSavesTests
{
    const string Id = "steam:1145360";
    static readonly JournalGate Clear = new(false, false, false, false);

    static GameRecord Game(string? slug = "tessela", GameStatus status = GameStatus.Finished) =>
        new() { Id = Id, DisplayName = "Tessela", Slug = slug, Status = status };

    static (AppState State, StatusSaves Saves, Func<int> Writes) With(GameRecord g)
    {
        var state = new AppState();
        state.Games[g.Id] = g;
        var writes = 0;
        return (state, new StatusSaves(state, () => writes++), () => writes);
    }

    static readonly StatusPick Shelved = new(PlayedStatus.Shelved, null, null, null);

    // The choice and its words

    [Fact]
    public void A_choice_reads_as_the_popup_shows_it()
    {
        Assert.Equal("Shelved", Shelved.Describe());
        Assert.Equal("Saved as Shelved.", Shelved.SavedBody());
        var mixed = new StatusPick(PlayedStatus.Shelved, null, true, false);
        Assert.Equal("Shelved, Backlog on, Wishlist off", mixed.Describe());
        Assert.Equal("Saved: Shelved, Backlog on, Wishlist off.", mixed.SavedBody());
        Assert.Equal("Saved: Playing on.", new StatusPick(null, true, null, null).SavedBody());
        Assert.Equal("Couldn't save Shelved for Tessela", Shelved.FailedTitle("Tessela"));
        Assert.True(new StatusPick(null, null, null, null).Empty);
        Assert.Equal("shelved", Shelved.StatusValue);
    }

    [Fact]
    public void Closed_says_under_a_minute_instead_of_0m()
    {
        Assert.Equal("Tessela closed, under a minute", Format.Closed("Tessela", TimeSpan.FromSeconds(40)));
        Assert.Equal("Tessela closed, 1m", Format.Closed("Tessela", TimeSpan.FromSeconds(60)));
        Assert.Equal("0m", Format.Minutes(0));
    }

    // The queue

    [Fact]
    public void A_choice_for_a_matched_game_is_queued_and_saved_by_the_next_run()
    {
        var g = Game();
        var (_, s, writes) = With(g);
        Assert.Equal(PickKept.Queued, s.Keep(Id, Shelved));
        Assert.True(writes() > 0);
        Assert.Equal(new StatusJob(Id, "tessela", Shelved), s.Next(Clear));
    }

    [Fact]
    public void Nothing_picked_keeps_nothing_and_an_unknown_game_neither()
    {
        var (_, s, _) = With(Game());
        Assert.Equal(PickKept.Nothing, s.Keep(Id, new StatusPick(null, null, null, null)));
        Assert.Equal(PickKept.Nothing, s.Keep("steam:2", Shelved));
        Assert.Null(s.Next(Clear));
    }

    [Fact]
    public void A_game_without_a_match_keeps_the_choice_until_it_has_one()
    {
        var g = Game(slug: null);
        var (_, s, _) = With(g);
        Assert.Equal(PickKept.NoMatch, s.Keep(Id, Shelved));
        Assert.Null(s.Next(Clear));
        Assert.Equal(Shelved, g.PendingPick);
        g.Slug = "tessela";
        s.Kick();
        Assert.Equal(Shelved, s.Next(Clear)!.Pick);
    }

    [Fact]
    public void A_newer_choice_replaces_an_older_one_not_yet_sent()
    {
        var g = Game();
        var (_, s, _) = With(g);
        s.Keep(Id, Shelved);
        var newer = new StatusPick(PlayedStatus.Completed, false, null, null);
        s.Keep(Id, newer);
        Assert.Equal(newer, s.Next(Clear)!.Pick);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void Never_runs_during_play_a_busy_screen_an_open_window_or_with_the_app_in_front(bool game, bool busy, bool window, bool front)
    {
        var (_, s, _) = With(Game());
        s.Keep(Id, Shelved);
        Assert.Null(s.Next(new JournalGate(game, busy, window, front)));
        Assert.NotNull(s.Next(Clear));
    }

    static JournalOutcome SavedAs(string status, bool playing) => new(JournalOutcomeKind.Saved) { SavedStatus = status, SavedPlaying = playing };

    [Fact]
    public void A_confirmed_save_clears_the_choice_and_updates_the_app_record()
    {
        var g = Game(status: GameStatus.Playing);
        g.PendingStart = new DateOnly(2026, 11, 1);
        var (_, s, _) = With(g);
        s.Keep(Id, Shelved);
        var job = s.Next(Clear)!;
        Assert.Equal(StatusNotice.Saved, s.Finish(job, SavedAs("shelved", false)));
        Assert.Null(g.PendingPick);
        Assert.Equal(GameStatus.Dropped, g.Status);
        Assert.Null(g.PendingStart);
        Assert.Null(s.Next(Clear));
    }

    [Fact]
    public void Three_failures_give_up_once_and_keep_the_choice_for_open_log()
    {
        var g = Game();
        var (_, s, _) = With(g);
        s.Keep(Id, Shelved);
        var notices = new List<StatusNotice>();
        for (var i = 0; i < Journal.MaxTries; i++)
        {
            s.Kick();
            notices.Add(s.Finish(s.Next(Clear)!, JournalOutcome.Failed("save response")));
        }
        Assert.Equal(new[] { StatusNotice.None, StatusNotice.None, StatusNotice.GaveUp }, notices);
        Assert.True(g.PickGaveUp);
        s.Kick();
        Assert.Null(s.Next(Clear));
        Assert.Equal(Shelved, s.TakeOver(Id));
        Assert.Null(g.PendingPick);
        Assert.False(g.PickGaveUp);
    }

    [Fact]
    public void A_game_with_no_log_gives_up_at_once()
    {
        var g = Game();
        var (_, s, _) = With(g);
        s.Keep(Id, Shelved);
        Assert.Equal(StatusNotice.GaveUp, s.Finish(s.Next(Clear)!, JournalOutcome.Unsupported("new log not supported yet")));
        Assert.True(g.PickGaveUp);
    }

    [Fact]
    public void A_stopped_run_is_tried_again_up_to_the_cap_without_counting()
    {
        var g = Game();
        var (_, s, _) = With(g);
        s.Keep(Id, Shelved);
        for (var i = 0; i < Journal.MaxStopsPerPass; i++)
            Assert.Equal(StatusNotice.None, s.Finish(s.Next(Clear)!, JournalOutcome.Stopped));
        Assert.Null(s.Next(Clear));
        Assert.Equal(0, g.PickTries);
    }

    [Fact]
    public void A_choice_replaced_during_a_run_is_saved_next_and_not_charged()
    {
        var g = Game();
        var (_, s, _) = With(g);
        s.Keep(Id, Shelved);
        var job = s.Next(Clear)!;
        var newer = new StatusPick(PlayedStatus.Completed, null, null, null);
        s.Keep(Id, newer);
        s.Finish(job, JournalOutcome.Failed("save response"));
        Assert.Equal((0, false), (g.PickTries, g.PickGaveUp));
        var next = s.Next(Clear)!;
        Assert.Equal(newer, next.Pick);
        Assert.Equal(StatusNotice.Saved, s.Finish(next, SavedAs("completed", false)));
        Assert.Null(g.PendingPick);
    }

    [Fact]
    public void Choices_round_trip_through_the_state_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bl-status-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var store = new StateStore(Path.Combine(dir, "state.json"));
            var state = store.Load();
            var g = Game();
            g.PendingPick = new StatusPick(PlayedStatus.Shelved, null, true, null);
            g.PickTries = 2;
            state.Games[g.Id] = g;
            store.Save(state);
            var again = new StateStore(Path.Combine(dir, "state.json")).Load().Games[Id];
            Assert.Equal((g.PendingPick, 2, false), (again.PendingPick, again.PickTries, again.PickGaveUp));
        }
        finally { Directory.Delete(dir, true); }
    }

    // Stale choices

    [Fact]
    public void A_choice_older_than_a_day_expires_once_and_open_log_still_has_it()
    {
        var g = Game();
        var now = new DateTime(2026, 11, 11, 12, 0, 0);
        var state = new AppState();
        state.Games[g.Id] = g;
        var s = new StatusSaves(state, () => { }, () => now);
        s.Keep(Id, Shelved);
        Assert.Equal(now, g.PendingPickAt);
        now = now.AddHours(23);
        Assert.Empty(s.Expire());
        now = now.AddHours(2);
        Assert.Equal(new[] { g }, s.Expire());
        Assert.True(g.PickGaveUp);
        Assert.Empty(s.Expire());
        s.Kick();
        Assert.Null(s.Next(Clear));
        Assert.Equal(Shelved, s.TakeOver(Id));
        Assert.Equal("Didn't save Shelved for Tessela", Shelved.ExpiredTitle("Tessela"));
    }

    [Fact]
    public void A_game_set_to_never_saves_nothing_and_loses_its_choice()
    {
        var g = Game();
        var (_, s, _) = With(g);
        s.Keep(Id, Shelved);
        g.Status = GameStatus.Never;
        Assert.Null(s.Next(Clear));
        Assert.Null(g.PendingPick);
    }

    [Fact]
    public void The_owners_editor_cookie_round_trips_through_the_state_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bl-cookie-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "state.json");
            var store = new StateStore(path);
            var state = store.Load();
            var saved = new EditorCookieSaved(true, "full", "backloggd.com", "/", new DateTime(2027, 11, 11, 0, 0, 0, DateTimeKind.Utc), "Strict", false, false);
            state.EditorCookieToRestore = saved;
            store.Save(state);
            Assert.Equal(saved, new StateStore(path).Load().EditorCookieToRestore);
            state.EditorCookieToRestore = new EditorCookieSaved(false);
            store.Save(state);
            Assert.Equal(new EditorCookieSaved(false), new StateStore(path).Load().EditorCookieToRestore);
            state.EditorCookieToRestore = null;
            store.Save(state);
            Assert.DoesNotContain("EditorCookieToRestore", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    // The save check

    static readonly JournalLogData Data = JournalLogData.Parse(JournalRequestTests.LoadedData)!;

    // The captured save, as a Mark as save would send it: no journal dates, totals as loaded,
    // and the log's status set to the pick.
    static List<KeyValuePair<string, string>> StatusSave(params (string Key, string Value)[] changes)
    {
        var f = JournalRequest.ParseBody("application/x-www-form-urlencoded", Encoding.UTF8.GetBytes(JournalRequestTests.CapturedSave))!
            .Where(x => !x.Key.StartsWith("dates[", StringComparison.Ordinal))
            .Select(x => x.Key switch
            {
                "playthroughs[0][hours_played]" or "log[total_hours]" => new KeyValuePair<string, string>(x.Key, "12"),
                "playthroughs[0][mins_played]" or "log[total_minutes]" => new KeyValuePair<string, string>(x.Key, "0"),
                _ => x,
            }).ToList();
        foreach (var (k, v) in changes)
        {
            var i = f.FindIndex(x => x.Key == k);
            if (i >= 0) f[i] = new(k, v);
            else f.Add(new(k, v));
        }
        return f;
    }

    static StatusExpect Expect(StatusPick? pick = null) => new("4242", pick ?? Shelved, Data, "605385");

    [Fact]
    public void Only_the_picked_status_changes()
    {
        Assert.Null(JournalRequest.CheckStatusSave(StatusSave(("log[status]", "shelved")), Expect()));
        // The page may set "played" with a status (it may only become true).
        Assert.Null(JournalRequest.CheckStatusSave(StatusSave(("log[status]", "shelved"), ("log[is_play]", "true")), Expect()));
    }

    [Fact]
    public void Picked_toggles_change_to_exactly_the_picked_value()
    {
        var pick = new StatusPick(null, false, true, null);
        Assert.Null(JournalRequest.CheckStatusSave(StatusSave(("log[is_playing]", "false"), ("log[is_backlog]", "true")), Expect(pick)));
        Assert.Equal("request log[is_backlog]: false sent, on picked", JournalRequest.CheckStatusSave(StatusSave(("log[is_playing]", "false")), Expect(pick)));
    }

    [Theory]
    [InlineData("log[status]", "completed", "request log[status]: completed sent, shelved picked")]
    [InlineData("log[is_playing]", "false", "request log[is_playing]: false sent, true loaded")]
    [InlineData("log[is_wishlist]", "true", "request log[is_wishlist]: true sent, false loaded")]
    [InlineData("log[rating]", "8", "request log[rating]: 8 sent, 0 loaded")]
    [InlineData("log[game_liked]", "true", "request log[game_liked]: true sent, false loaded")]
    [InlineData("log[total_hours]", "13", "request log: total_hours 13/0 sent, 12/0 loaded, expected 720m")]
    [InlineData("playthroughs[0][review]", "Changed", "request playthroughs[0][review]: differs from the log data")]
    [InlineData("playthroughs[0][rating]", "8", "request playthroughs[0][rating]: differs from the log data")]
    [InlineData("playthroughs[0][hours_played]", "13", "request playthroughs[0]: hours_played 13/0 sent, 12/0 loaded, expected 720m")]
    [InlineData("game_id", "1", "request game_id")]
    public void Any_other_change_is_refused(string key, string value, string why)
    {
        Assert.Equal(why, JournalRequest.CheckStatusSave(StatusSave(("log[status]", "shelved"), (key, value)), Expect()));
    }

    [Fact]
    public void Played_can_only_be_set_by_a_status_and_only_to_true()
    {
        // Only a toggle picked: "played" stays as loaded.
        Assert.Equal("request log[is_play]: true sent, false loaded",
            JournalRequest.CheckStatusSave(StatusSave(("log[is_play]", "true"), ("log[is_backlog]", "true")), Expect(new StatusPick(null, null, true, null))));
        // A status picked: "played" may go to true, never back to false from true.
        var playedLoaded = JournalLogData.Parse(JournalRequestTests.LoadedData.Replace("\"is_play\":false", "\"is_play\":true"))!;
        Assert.Equal("request log[is_play]: false sent, true loaded",
            JournalRequest.CheckStatusSave(StatusSave(("log[status]", "shelved"), ("log[is_play]", "false")), Expect() with { Data = playedLoaded }));
    }

    [Theory]
    [InlineData("dates[901][0][id]", "77", "journal dates in a status save")]
    [InlineData("log[review]", "x", "unknown log key log[review]")]
    [InlineData("playthroughs[0][notes]", "x", "unknown playthrough key")]
    [InlineData("authenticity_token", "abc", "unknown key authenticity_token")]
    [InlineData("deleted_dates", "17557166", "removes")]
    public void Extra_keys_are_refused_by_name(string key, string value, string why)
    {
        var f = StatusSave(("log[status]", "shelved"));
        f.Add(new(key, value));
        Assert.Contains(why, JournalRequest.CheckStatusSave(f, Expect()));
    }

    [Fact]
    public void Missing_keys_or_data_are_refused()
    {
        Assert.Equal("request log[status] missing", JournalRequest.CheckStatusSave(StatusSave().Where(x => x.Key != "log[status]").ToList(), Expect()));
        Assert.Equal("request body unreadable", JournalRequest.CheckStatusSave(null, Expect()));
        Assert.Equal("log data unreadable", JournalRequest.CheckStatusSave(StatusSave(), Expect() with { Data = null }));
    }

    static Func<(string?, byte[]?)> Raw(List<KeyValuePair<string, string>> f) =>
        () => ("application/x-www-form-urlencoded", Encoding.UTF8.GetBytes(string.Join("&", f.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)))));

    [Fact]
    public void The_traffic_filter_lets_one_status_save_out_for_the_armed_game_only()
    {
        const string url = "https://backloggd.com/api/user/1/log/4242";
        var body = StatusSave(("log[status]", "shelved"));
        var before = new JournalTraffic();
        Assert.False(before.Request("POST", url, Raw(body)).Allow);
        Assert.Equal("save before Save was pressed", before.Refused);

        var wrongGame = new JournalTraffic();
        wrongGame.ArmStatus(Expect());
        Assert.False(wrongGame.Request("POST", "https://backloggd.com/api/user/1/log/1", Raw(body)).Allow);

        var t = new JournalTraffic();
        t.ArmStatus(Expect());
        Assert.True(t.Armed);
        Assert.Equal("4242", t.ArmedGameId);
        Assert.True(t.Request("POST", url, Raw(body)).Allow);
        Assert.False(t.Request("POST", url, Raw(body)).Allow);
        Assert.Equal("a second save", t.Refused);

        var refused = new JournalTraffic();
        refused.ArmStatus(Expect());
        Assert.False(refused.Request("POST", url, Raw(StatusSave(("log[status]", "completed")))).Allow);
        Assert.Contains("completed sent, shelved picked", refused.Refused);
    }

    [Fact]
    public void The_script_message_for_a_status_save_parses()
    {
        var r = JournalScriptResult.Parse("""{"type":"journal","ok":true,"statusReady":true,"gameId":"4242","log":{"status":"completed"},"after":{"status":"shelved","is_playing":"false"},"coverId":"605385"}""");
        Assert.Equal(JournalScriptKind.StatusReady, r.Kind);
        Assert.Equal(("completed", "shelved", "false", "605385"), (r.Log["status"], r.After["status"], r.After["is_playing"], r.CoverId));
        Assert.Equal("message", JournalScriptResult.Parse("""{"type":"journal","ok":true,"statusReady":true,"log":{},"after":{}}""").Step);
        Assert.Equal("message", JournalScriptResult.Parse("""{"type":"journal","ok":true,"statusReady":true,"gameId":"1","log":{}}""").Step);
    }
}

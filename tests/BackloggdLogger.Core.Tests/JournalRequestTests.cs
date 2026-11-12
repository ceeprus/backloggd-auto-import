using System.Text;
using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class JournalRequestTests
{
    static readonly DateOnly Day = new(2026, 11, 8);

    // The save, held against a real one

    // A real save captured on 2026-11-09 (blocked in the page, nothing sent), with ids anonymized and
    // the session changed from 5h to 6h 35m: the entry's time and the played totals rise by 95m.
    internal const string CapturedSave =
        "game_id=4242" +
        "&playthroughs%5B0%5D%5Bid%5D=901&playthroughs%5B0%5D%5Bcreated_at%5D=Nov+8+2026&playthroughs%5B0%5D%5Bupdated_at%5D=Nov+9+2026" +
        "&playthroughs%5B0%5D%5Brating%5D=0&playthroughs%5B0%5D%5Breview%5D=&playthroughs%5B0%5D%5Breview_spoilers%5D=false" +
        "&playthroughs%5B0%5D%5Bmedium_id%5D=&playthroughs%5B0%5D%5Bstorefront_id%5D=&playthroughs%5B0%5D%5Bplatform%5D=&playthroughs%5B0%5D%5Bplayed_platform%5D=" +
        "&playthroughs%5B0%5D%5Bsync_sessions%5D=true&playthroughs%5B0%5D%5Bhours_played%5D=13&playthroughs%5B0%5D%5Bmins_played%5D=35" +
        "&playthroughs%5B0%5D%5Bhours_finished%5D=0&playthroughs%5B0%5D%5Bmins_finished%5D=0&playthroughs%5B0%5D%5Bhours_mastered%5D=0&playthroughs%5B0%5D%5Bmins_mastered%5D=0" +
        "&playthroughs%5B0%5D%5Bis_master%5D=false&playthroughs%5B0%5D%5Bis_replay%5D=false&playthroughs%5B0%5D%5Btitle%5D=Log" +
        "&playthroughs%5B0%5D%5Bstart_date%5D=&playthroughs%5B0%5D%5Bfinish_date%5D=&playthroughs%5B0%5D%5Bedition_id%5D=&playthroughs%5B0%5D%5Bedition_type%5D=" +
        "&dates%5B901%5D%5B0%5D%5Bid%5D=77&dates%5B901%5D%5B0%5D%5Brange_start_date%5D=2026-11-08&dates%5B901%5D%5B0%5D%5Brange_end_date%5D=2026-11-09" +
        "&dates%5B901%5D%5B0%5D%5Bedited%5D=true&dates%5B901%5D%5B0%5D%5Bstatus%5D=5&dates%5B901%5D%5B0%5D%5Bnote%5D=" +
        "&dates%5B901%5D%5B0%5D%5Bhours%5D=6&dates%5B901%5D%5B0%5D%5Bminutes%5D=35" +
        "&dates%5B901%5D%5B0%5D%5Bstart_date%5D=&dates%5B901%5D%5B0%5D%5Bfinish_date%5D=&dates%5B901%5D%5B0%5D%5Bprivacy%5D=" +
        "&log%5Bid%5D=55&log%5Bstatus%5D=completed&log%5Brating%5D=0&log%5Bis_play%5D=false&log%5Bis_playing%5D=true&log%5Bis_backlog%5D=false" +
        "&log%5Bis_wishlist%5D=false&log%5Bgame_liked%5D=false&log%5Btime_source%5D=1&log%5Btotal_hours%5D=13&log%5Btotal_minutes%5D=35" +
        "&log%5Bavg_finish_time%5D=&log%5Bavg_master_time%5D=&log%5Boverride_cover_id%5D=605385&log%5Blast_edited_at%5D=1794203781" +
        "&modal_type=quick";

    // The GET /log/edit/ answer loaded before Save, in the shape read live (a read-only fetch on
    // 2026-11-09), ids anonymized: the log under game_log, playthroughs keyed by id.
    internal const string LoadedData = """
        {"game_log":{"id":55,"status":"completed","rating":0,"is_play":false,"is_playing":true,"is_backlog":false,"is_wishlist":false,"game_liked":false,
          "time_source":1,"total_hours":12,"total_minutes":0,"avg_finish_time":null,"avg_master_time":null,"override_cover_id":null,
          "last_edited_at":1794203781,"time_entries_editions":[]},
         "most_recent_playthrough_id":901,
         "playthroughs":{"901":{"id":901,"created_at":"Nov 8 2026","updated_at":"Nov 9 2026","rating":0,"review":"","review_spoilers":false,
           "medium_id":null,"storefront_id":null,"platform":null,"played_platform":null,"sync_sessions":true,"hours_played":12,"mins_played":0,
           "hours_finished":0,"mins_finished":0,"hours_mastered":0,"mins_mastered":0,"is_master":false,"is_replay":false,"title":"Log",
           "start_date":null,"finish_date":null,"edition_id":null,"edition_type":null,
           "play_dates":[{"id":77,"range_start_date":"2026-11-08","range_end_date":"2026-11-09","edited":false,"status":null,"note":"","hours":5,"minutes":0,
             "start_date":null,"finish_date":null,"privacy":null,"tags":[]}]}}}
        """;

    // The /open-date-modal/ request for that session: the day as "Nov 8" with no year, the last day
    // inclusive, minutes "0" while the dialog shows "", status empty.
    static readonly JournalDialog Session = new("77", new Dictionary<string, string?>
    {
        ["privacy"] = "", ["note"] = "", ["status"] = "", ["id"] = "77", ["hours"] = "5", ["finish_date"] = "", ["start_date"] = "",
        ["minutes"] = "0", ["start"] = "Nov 8", ["end"] = "Nov 8", ["disableStart"] = "false", ["disableFinish"] = "false",
    });

    // The log box's checked cover choice, which the save sends while the log data has null.
    const string Cover = "605385";

    static JournalExpect Expect(int current = 300, int target = 395, bool boxes = false, string? data = LoadedData, string? cover = Cover) =>
        new("4242", Day, current, target, Session, boxes, JournalLogData.Parse(data), cover);

    static List<KeyValuePair<string, string>> Captured() =>
        JournalRequest.ParseBody("application/x-www-form-urlencoded; charset=UTF-8", Encoding.UTF8.GetBytes(CapturedSave))!;

    static List<KeyValuePair<string, string>> With(string key, string value) =>
        Captured().Select(x => x.Key == key ? new KeyValuePair<string, string>(key, value) : x).ToList();

    static List<KeyValuePair<string, string>> Plus(string key, string value) => [.. Captured(), new(key, value)];

    static List<KeyValuePair<string, string>> Without(string key) => Captured().Where(x => x.Key != key).ToList();

    static string? Check(List<KeyValuePair<string, string>>? f, JournalExpect? x = null) => JournalRequest.CheckSave(f, x ?? Expect());

    [Fact]
    public void The_captured_save_for_5h_to_6h35m_goes_out()
    {
        // game_id, 24 playthrough keys, 11 date entry keys (no tags), 15 log keys, modal_type.
        Assert.Equal(1 + 24 + 11 + 15 + 1, Captured().Count);
        Assert.Null(Check(Captured()));
    }

    [Theory]
    [InlineData("playthroughs[0][rating]", "8", "differs from the log data")]
    [InlineData("playthroughs[0][review]", "Changed", "differs from the log data")]
    [InlineData("playthroughs[0][review_spoilers]", "true", "differs from the log data")]
    [InlineData("log[status]", "abandoned", "differs from the log data")]
    [InlineData("log[rating]", "8", "differs from the log data")]
    [InlineData("log[is_playing]", "false", "differs from the log data")]
    [InlineData("log[game_liked]", "true", "differs from the log data")]
    [InlineData("log[last_edited_at]", "1794203999", "differs from the log data")]
    [InlineData("playthroughs[0][title]", "Replay", "differs from the log data")]
    [InlineData("dates[901][0][note]", "Boss fight", "dialog had")]
    [InlineData("dates[901][0][privacy]", "1", "dialog had")]
    [InlineData("dates[901][0][status]", "1", "dialog had")]
    [InlineData("dates[901][0][hours]", "7", "time")]
    [InlineData("dates[901][0][id]", "78", "session id")]
    [InlineData("dates[901][0][range_start_date]", "2026-11-07", "day")]
    [InlineData("dates[901][0][range_end_date]", "2026-11-08", "range_end_date")]
    [InlineData("dates[901][0][edited]", "false", "not edited")]
    [InlineData("game_id", "1", "game_id")]
    [InlineData("modal_type", "full", "modal_type")]
    public void A_changed_value_in_the_captured_save_is_refused(string key, string value, string why)
    {
        Assert.Contains(why, Check(With(key, value)));
    }

    [Theory]
    [InlineData("playthroughs[0][hours_played]", "12", "hours_played 12/35 sent, 12/0 loaded, expected 720m or 815m")]
    [InlineData("playthroughs[0][mins_played]", "0", "expected 720m or 815m")]
    [InlineData("playthroughs[0][mins_played]", "36", "expected 720m or 815m")]
    [InlineData("log[total_hours]", "14", "expected 720m or 815m")]
    [InlineData("log[total_minutes]", "0", "expected 720m or 815m")]
    [InlineData("log[total_minutes]", "", "expected 720m or 815m")]
    public void Played_totals_may_only_stay_or_rise_by_the_minutes_added(string key, string value, string why)
    {
        Assert.Contains(why, Check(With(key, value)));
    }

    [Fact]
    public void A_total_may_rise_only_while_its_switch_is_on()
    {
        var noSync = LoadedData.Replace("\"sync_sessions\":true", "\"sync_sessions\":false");
        var manualLog = LoadedData.Replace("\"time_source\":1", "\"time_source\":0");
        // The body's switches must equal the log data too, so they follow it.
        var body = With("playthroughs[0][sync_sessions]", "false");
        Assert.Equal("request playthroughs[0]: hours_played 13/35 sent, 12/0 loaded, expected 720m", Check(body, Expect(data: noSync)));
        Assert.Equal("request log: total_hours 13/35 sent, 12/0 loaded, expected 720m", Check(With("log[time_source]", "0"), Expect(data: manualLog)));
        var kept = body.Select(x => x.Key switch
        {
            "playthroughs[0][hours_played]" => new KeyValuePair<string, string>(x.Key, "12"),
            "playthroughs[0][mins_played]" => new KeyValuePair<string, string>(x.Key, "0"),
            _ => x,
        }).ToList();
        Assert.Null(Check(kept, Expect(data: noSync)));
    }

    [Fact]
    public void A_refusal_that_failed_keeps_the_target()
    {
        var t = Armed();
        Assert.False(t.Request("POST", SaveUrl, Raw(CapturedSave.Replace("%5Bhours%5D=6", "%5Bhours%5D=9"))).Allow);
        Assert.Null(new JournalRun().Close(JournalOutcome.Failed("refused"), t, 395).Target);
        t.MaybeSent = true;
        Assert.Equal(JournalOutcome.Failed("refused", 395), new JournalRun().Close(JournalOutcome.Failed("refused"), t, 395));
        var sent = Armed();
        Assert.True(sent.Request("POST", SaveUrl, Raw(CapturedSave)).Allow);
        Assert.Equal(395, new JournalRun().Close(JournalOutcome.Stopped, sent, 395).Target);
        Assert.Null(new JournalRun().Close(JournalOutcome.Stopped, new JournalTraffic(), 395).Target);
    }

    [Fact]
    public void Played_totals_that_stay_put_are_accepted_each_on_its_own()
    {
        static List<KeyValuePair<string, string>> Kept(params string[] groups) => Captured().Select(x => x.Key switch
        {
            "playthroughs[0][hours_played]" when groups.Contains("playthrough") => new KeyValuePair<string, string>(x.Key, "12"),
            "playthroughs[0][mins_played]" when groups.Contains("playthrough") => new KeyValuePair<string, string>(x.Key, "0"),
            "log[total_hours]" when groups.Contains("log") => new KeyValuePair<string, string>(x.Key, "12"),
            "log[total_minutes]" when groups.Contains("log") => new KeyValuePair<string, string>(x.Key, "0"),
            _ => x,
        }).ToList();
        Assert.Null(Check(Kept("playthrough", "log")));
        Assert.Null(Check(Kept("playthrough")));
        Assert.Null(Check(Kept("log")));
    }

    [Theory]
    [InlineData("playthroughs[0][notes]", "x", "unknown playthrough key")]
    [InlineData("log[review]", "x", "unknown log key")]
    [InlineData("review", "x", "unknown key")]
    [InlineData("authenticity_token", "abc", "unknown key")]
    [InlineData("_method", "patch", "unknown key")]
    [InlineData("dates[901][0][rating]", "3", "unknown field")]
    [InlineData("dates[901][0][_destroy]", "1", "unknown field")]
    [InlineData("playthroughs[0][_destroy]", "1", "unknown playthrough key")]
    [InlineData("destroy_log", "1", "removes")]
    [InlineData("deleted_dates", "17557166", "removes")]
    [InlineData("deleted_playthroughs", "901", "removes")]
    [InlineData("deleted_dates[0]", "1", "removes")]
    [InlineData("game_id", "4242", "twice")]
    public void An_extra_key_is_refused_by_name(string key, string value, string why)
    {
        Assert.Contains(why, Check(Plus(key, value)));
    }

    [Theory]
    [InlineData("dates[901][0][note]", "request dates: note missing")]
    [InlineData("dates[901][0][status]", "request dates: status missing")]
    [InlineData("dates[901][0][privacy]", "request dates: privacy missing")]
    [InlineData("dates[901][0][range_end_date]", "request dates: range_end_date missing")]
    [InlineData("dates[901][0][finish_date]", "request dates: finish_date missing")]
    [InlineData("playthroughs[0][rating]", "request playthroughs[0][rating] missing")]
    [InlineData("playthroughs[0][review]", "request playthroughs[0][review] missing")]
    [InlineData("playthroughs[0][id]", "request playthroughs[0][id] missing")]
    [InlineData("log[rating]", "request log[rating] missing")]
    [InlineData("log[status]", "request log[status] missing")]
    [InlineData("log[last_edited_at]", "request log[last_edited_at] missing")]
    public void A_group_must_carry_its_whole_captured_key_set(string key, string why)
    {
        Assert.Equal(why, Check(Without(key)));
    }

    // Tags, as a list, the session's own

    static string Tagged(string tags) => LoadedData.Replace("\"tags\":[]", "\"tags\":" + tags);

    static List<KeyValuePair<string, string>> WithTags(params string[] tags) =>
        [.. Captured(), .. tags.Select(t => new KeyValuePair<string, string>("dates[901][0][tags][]", t))];

    [Fact]
    public void A_session_with_tags_saves_them_as_the_same_list()
    {
        var data = Tagged("""["boss","co-op"]""");
        Assert.Null(Check(WithTags("boss", "co-op"), Expect(data: data)));
        var body = string.Join("&", WithTags("boss", "co-op").Select(f => Uri.EscapeDataString(f.Key) + "=" + Uri.EscapeDataString(f.Value)));
        var t = new JournalTraffic();
        t.Arm(Expect(data: data));
        Assert.True(t.Request("POST", SaveUrl, Raw(body)).Allow);
    }

    [Theory]
    [InlineData("co-op", "boss")]
    [InlineData("boss")]
    [InlineData("boss", "co-op", "speedrun")]
    [InlineData()]
    public void Tags_that_differ_in_items_or_order_are_refused(params string[] sent)
    {
        Assert.Equal($"request dates: tags [{string.Join(",", sent)}] sent, [boss,co-op] in the log data", Check(WithTags(sent), Expect(data: Tagged("""["boss","co-op"]"""))));
    }

    [Fact]
    public void No_tags_in_the_log_data_means_no_tags_key()
    {
        Assert.Null(Check(Captured()));
        Assert.Equal("request dates: tags [boss] sent, [] in the log data", Check(WithTags("boss")));
        Assert.Equal("request dates: unknown field tags", Check(Plus("dates[901][0][tags]", "boss")));
        Assert.Equal("request dates: tags of another entry", Check(Plus("dates[902][0][tags][]", "boss")));
    }

    [Fact]
    public void Tags_the_log_data_holds_as_objects_are_unreadable()
    {
        Assert.Equal("request dates: session tags unreadable in the log data", Check(Captured(), Expect(data: Tagged("""[{"name":"boss"}]"""))));
        Assert.Equal(new[] { "boss", "7" }, JournalLogData.Parse(Tagged("""["boss",7]"""))!.Session("77")!.Tags);
    }

    [Theory]
    [InlineData("deleted_dates")]
    [InlineData("deleted_playthroughs")]
    [InlineData("deleted_library_entries")]
    [InlineData("deleted_edition_time_entries")]
    public void An_empty_deleted_list_is_accepted(string key)
    {
        Assert.Null(Check(Plus(key, "")));
    }

    [Fact]
    public void A_second_date_entry_or_none_is_refused()
    {
        Assert.Contains("2 entries", Check(Plus("dates[901][1][edited]", "true")));
        Assert.Contains("0 entries", Check(Captured().Where(x => !x.Key.StartsWith("dates")).ToList()));
        Assert.Contains("not in the log data", Check(Captured().Select(x => x.Key.StartsWith("dates[901]") ? new KeyValuePair<string, string>(x.Key.Replace("dates[901]", "dates[902]"), x.Value) : x).ToList()));
    }

    [Fact]
    public void Status_5_is_what_the_dialogs_save_sets_from_empty_or_5_whatever_the_boxes()
    {
        Assert.Null(Check(Captured()));
        Assert.Null(Check(Captured(), Expect(boxes: true)));
        var five = new JournalDialog("77", new Dictionary<string, string?>(Session.Values) { ["status"] = "5" });
        Assert.Null(Check(Captured(), Expect(boxes: true) with { Dialog = five }));
        var withStatus = new JournalDialog("77", new Dictionary<string, string?>(Session.Values) { ["status"] = "2" });
        Assert.Equal("request dates: status \"5\", dialog had \"2\"", Check(Captured(), Expect() with { Dialog = withStatus }));
        Assert.Null(Check(With("dates[901][0][status]", "2"), Expect() with { Dialog = withStatus }));
        Assert.Contains("status", Check(With("dates[901][0][status]", "3")));
    }

    [Theory]
    [InlineData("2026-11-01", "2026-11-01")]
    [InlineData("Nov 1, 2026", "2026-11-01")]
    [InlineData("2026-11-01", "Nov 1")]
    [InlineData("2026-11-01T00:00:00.000Z", "Nov 1, 2026")]
    public void Session_dates_match_across_spellings(string sent, string had)
    {
        var dialog = new JournalDialog("77", new Dictionary<string, string?>(Session.Values) { ["start_date"] = had });
        Assert.Null(Check(With("dates[901][0][start_date]", sent), Expect() with { Dialog = dialog }));
    }

    [Fact]
    public void A_session_date_mismatch_names_both_raw_values()
    {
        var dialog = new JournalDialog("77", new Dictionary<string, string?>(Session.Values) { ["finish_date"] = "Nov 2" });
        Assert.Equal("request dates: finish_date \"2026-11-01\", dialog had \"Nov 2\"", Check(With("dates[901][0][finish_date]", "2026-11-01"), Expect() with { Dialog = dialog }));
        Assert.Equal("request dates: start_date \"Nov 1\", dialog had \"\"", Check(With("dates[901][0][start_date]", "Nov 1")));
    }


    [Fact]
    public void The_entry_must_sit_under_the_playthrough_that_holds_the_session()
    {
        const string second = """
            ,"902":{"id":902,"created_at":"Nov 1 2026","updated_at":"Nov 1 2026","rating":0,"review":"",
            "review_spoilers":false,"medium_id":null,"storefront_id":null,"platform":null,"played_platform":null,"sync_sessions":true,"hours_played":3,
            "mins_played":0,"hours_finished":0,"mins_finished":0,"hours_mastered":0,"mins_mastered":0,"is_master":false,"is_replay":true,"title":"Replay",
            "start_date":null,"finish_date":null,"edition_id":null,"edition_type":null,"play_dates":[{"id":88}]}}}
            """;
        var two = LoadedData.TrimEnd()[..^2] + second;
        Assert.NotNull(JournalLogData.Parse(two));
        Assert.Null(Check(Captured(), Expect(data: two)));
        var moved = Captured().Select(x => x.Key.StartsWith("dates[901]") ? new KeyValuePair<string, string>(x.Key.Replace("dates[901]", "dates[902]"), x.Value) : x).ToList();
        Assert.Equal("request dates: playthrough 902 doesn't hold session 77", Check(moved, Expect(data: two)));
        var notHeld = LoadedData.Replace("""[{"id":77,""", """[{"id":78,""");
        Assert.Equal("request dates: playthrough 901 doesn't hold session 77", Check(Captured(), Expect(data: notHeld)));
    }

    [Fact]
    public void A_playthrough_or_log_value_missing_from_the_log_data_is_refused()
    {
        Assert.Contains("not in the log data", Check(Captured(), Expect(data: LoadedData.Replace("\"edition_type\":null,", ""))));
        Assert.Contains("not in the log data", Check(Captured(), Expect(data: LoadedData.Replace("\"avg_master_time\":null,", ""))));
        Assert.Contains("playthrough 902 not in the log data", Check(With("playthroughs[0][id]", "902")));
    }

    [Fact]
    public void Without_readable_log_data_nothing_goes_out()
    {
        Assert.Equal("log data unreadable", Check(Captured(), Expect(data: "not json")));
        Assert.Equal("log data unreadable", Check(Captured(), Expect(data: """{"status":"completed"}""")));
        Assert.Equal("request body unreadable", Check(null));
    }

    [Fact]
    public void Log_data_reads_the_live_shape_and_keeps_scalars_only()
    {
        var data = JournalLogData.Parse(LoadedData)!;
        Assert.Equal("completed", data.Log["status"]);
        Assert.Equal("false", data.Log["is_play"]);
        Assert.Equal("", data.Log["avg_finish_time"]);
        Assert.Equal("", data.Log["override_cover_id"]);
        Assert.False(data.Log.ContainsKey("time_entries_editions"));
        var p = Assert.Single(data.Playthroughs);
        Assert.Equal(("901", "12", "true", ""), (p["id"], p["hours_played"], p["sync_sessions"], p["platform"]));
        Assert.False(p.ContainsKey("play_dates"));
        Assert.Equal(new[] { "77" }, data.PlayDates["901"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"status":"completed","playthroughs":{"1":{"id":1}}}""")]
    [InlineData("""{"log":{"status":"completed"},"playthroughs":{"1":{"id":1}}}""")]
    [InlineData("""{"game_log":{"status":"completed"},"playthroughs":[{"id":1}]}""")]
    [InlineData("""{"game_log":{"status":"completed"}}""")]
    [InlineData("""{"game_log":[],"playthroughs":{}}""")]
    [InlineData("""{"game_log":{"status":"completed"},"playthroughs":{"1":{"id":2}}}""")]
    [InlineData("""{"game_log":{"status":"completed"},"playthroughs":{"1":{"title":"Log"}}}""")]
    [InlineData("""{"game_log":{"status":"completed"},"playthroughs":{"1":5}}""")]
    [InlineData("""{"game_log":{"status":"completed"},"playthroughs":{"1":{"id":1,"play_dates":{"id":2}}}}""")]
    public void Any_other_log_data_shape_is_unreadable(string json)
    {
        Assert.Null(JournalLogData.Parse(json));
    }

    [Fact]
    public void The_cover_may_be_the_log_datas_or_the_checked_choice_and_nothing_else()
    {
        Assert.Null(Check(Captured()));
        Assert.Contains("override_cover_id", Check(Captured(), Expect(cover: null)));
        Assert.Contains("override_cover_id", Check(With("log[override_cover_id]", "605386")));
        Assert.Null(Check(With("log[override_cover_id]", ""), Expect(cover: null)));
        Assert.Null(Check(With("log[override_cover_id]", ""), Expect(cover: "605385")));
        var withCover = LoadedData.Replace("\"override_cover_id\":null", "\"override_cover_id\":605385");
        Assert.Null(Check(Captured(), Expect(data: withCover, cover: null)));
    }

    [Theory]
    [InlineData("playthroughs[0][platform]", "6")]
    [InlineData("playthroughs[0][hours_finished]", "")]
    [InlineData("playthroughs[0][sync_sessions]", "1")]
    [InlineData("log[is_play]", "0")]
    [InlineData("log[avg_finish_time]", "0")]
    public void Null_is_only_empty_and_a_boolean_only_its_word(string key, string value)
    {
        Assert.Contains("differs from the log data", Check(With(key, value)));
    }

    // The log box must be a real, loaded log

    [Theory]
    [InlineData(GameStatus.Finished, "completed", "completed", null)]
    [InlineData(GameStatus.Dropped, "shelved", "shelved", null)]
    [InlineData(GameStatus.Dropped, "retired", "retired", null)]
    [InlineData(GameStatus.Dropped, "completed", "completed", null)]
    [InlineData(GameStatus.Playing, "played", "played", null)]
    [InlineData(GameStatus.Finished, "", "", null)]
    [InlineData(GameStatus.Finished, "", null, null)]
    [InlineData(GameStatus.Playing, "", "completed", "log data has one")]
    [InlineData(GameStatus.Never, "played", "played", "app status")]
    [InlineData(GameStatus.Unknown, "played", "played", "app status")]
    public void The_game_must_be_logged_in_the_app_and_the_box_as_loaded(GameStatus app, string status, string? loaded, string? why)
    {
        var step = JournalRequest.LogFits(app, new Dictionary<string, string?> { ["status"] = status, ["is_playing"] = "false" }, loaded);
        if (why is null) Assert.Null(step);
        else Assert.Contains(why, step);
        Assert.Null(JournalRequest.LogFits(GameStatus.Dropped, new Dictionary<string, string?>(), null));
    }

    // Played totals in any format the page writes

    static List<KeyValuePair<string, string>> Totals(string ph, string pm, string lh, string lm) => Captured().Select(x => x.Key switch
    {
        "playthroughs[0][hours_played]" => new KeyValuePair<string, string>(x.Key, ph),
        "playthroughs[0][mins_played]" => new KeyValuePair<string, string>(x.Key, pm),
        "log[total_hours]" => new KeyValuePair<string, string>(x.Key, lh),
        "log[total_minutes]" => new KeyValuePair<string, string>(x.Key, lm),
        _ => x,
    }).ToList();

    [Theory]
    [InlineData("12.0", "0", "12", "0.0")]
    [InlineData("13", "35.0", "13.00", "35")]
    [InlineData("13.5", "5", "13", "35")]
    [InlineData("13", "35.4", "13", "35")]
    [InlineData(" 12 ", "", "12", "")]
    public void Totals_read_as_decimals_and_pass_unchanged_or_raised(string ph, string pm, string lh, string lm)
    {
        Assert.Null(Check(Totals(ph, pm, lh, lm)));
    }

    [Fact]
    public void Empty_totals_sent_as_loaded_pass_whatever_their_format()
    {
        var empty = LoadedData.Replace("\"hours_played\":12,\"mins_played\":0", "\"hours_played\":null,\"mins_played\":null")
            .Replace("\"total_hours\":12,\"total_minutes\":0", "\"total_hours\":null,\"total_minutes\":null");
        Assert.Null(Check(Totals("", "", "", ""), Expect(data: empty)));
        Assert.Null(Check(Totals("1", "35", "1", "35"), Expect(data: empty)));
        Assert.Equal("request log: total_hours 2/0 sent, / loaded, expected 0m or 95m", Check(Totals("1", "35", "2", "0"), Expect(data: empty)));
    }

    [Theory]
    [InlineData("13", "36", "13", "35", "request playthroughs[0]: hours_played 13/36 sent, 12/0 loaded, expected 720m or 815m")]
    [InlineData("13", "35", "4.5", "30", "request log: total_hours 4.5/30 sent, 12/0 loaded, expected 720m or 815m")]
    [InlineData("-1", "35", "13", "35", "request playthroughs[0]: hours_played -1/35 sent, 12/0 loaded, unreadable")]
    [InlineData("13", "35", "13", "a", "request log: total_hours 13/a sent, 12/0 loaded, unreadable")]
    [InlineData("13", "35", "1e3", "35", "request log: total_hours 1e3/35 sent, 12/0 loaded, unreadable")]
    public void A_refused_total_names_its_four_raw_values(string ph, string pm, string lh, string lm, string why)
    {
        Assert.Equal(why, Check(Totals(ph, pm, lh, lm)));
    }

    // The editor mode cookie

    [Theory]
    [InlineData("full", true, true, "full")]
    [InlineData(null, true, true, null)]
    [InlineData("mobile", true, true, "mobile")]
    [InlineData("", true, true, "")]
    [InlineData("quick", false, false, null)]
    public void A_run_switches_the_editor_to_quick_and_puts_the_owners_value_back(string? existing, bool sw, bool restore, string? value)
    {
        Assert.Equal((sw, restore, value), JournalEditorMode.Plan(existing));
    }

    // The save's answer

    [Theory]
    [InlineData(200, "application/json", """{"id":5}""", false, null)]
    [InlineData(200, "text/javascript", "window.location.reload();", false, null)]
    [InlineData(204, null, "", false, null)]
    [InlineData(302, null, "", false, "redirect")]
    [InlineData(401, "application/json", "{}", false, "status 401")]
    [InlineData(200, "text/html; charset=utf-8", "<!doctype html><title>Sign in</title>", false, "web page")]
    [InlineData(200, null, "  <html>", false, "web page")]
    [InlineData(200, "application/json", """{"errors":["Rating is invalid"]}""", false, "error")]
    [InlineData(200, "application/json", """{"success":false}""", false, "not saved")]
    [InlineData(200, "application/json", """{"errors":[]}""", false, null)]
    [InlineData(200, "application/json", "{", false, "broken")]
    [InlineData(200, "application/json", "{}", true, "sign-in")]
    [InlineData(200, "application/json", null, false, "unreadable")]
    public void Only_a_plain_successful_answer_counts_as_saved(int status, string? type, string? body, bool signIn, string? why)
    {
        var step = JournalRequest.CheckSaveAnswer(status, type, body, signIn);
        if (why is null) Assert.Null(step);
        else Assert.Contains(why, step);
    }

    // Bodies and dates

    [Fact]
    public void Bodies_parse_as_urlencoded_only()
    {
        var url = JournalRequest.ParseBody("application/x-www-form-urlencoded; charset=UTF-8", Encoding.UTF8.GetBytes("log%5Bstatus%5D=completed&play_data%5Bstart%5D=Nov+8&x="))!;
        Assert.Equal(new[] { "log[status]=completed", "play_data[start]=Nov 8", "x=" }, url.Select(p => $"{p.Key}={p.Value}"));
        Assert.NotNull(JournalRequest.ParseBody("Application/X-WWW-Form-Urlencoded", Encoding.UTF8.GetBytes("a=1")));
    }

    [Theory]
    [InlineData("multipart/form-data; boundary=b1", "--b1\r\nContent-Disposition: form-data; name=\"log[status]\"\r\n\r\ncompleted\r\n--b1--\r\n")]
    [InlineData("application/json", "{\"log\":{\"status\":\"completed\"}}")]
    [InlineData("text/plain", "a=1")]
    [InlineData(null, "a=1")]
    [InlineData("", "a=1")]
    [InlineData("application/x-www-form-urlencoded-x", "a=1")]
    public void Any_other_body_is_unreadable(string? type, string body)
    {
        Assert.Null(JournalRequest.ParseBody(type, Encoding.UTF8.GetBytes(body)));
        Assert.Null(JournalRequest.ParseBody(type, null));
    }

    [Fact]
    public void A_save_that_isnt_urlencoded_is_refused()
    {
        var t = Armed();
        Assert.False(t.Request("POST", SaveUrl, () => ("multipart/form-data; boundary=b1", Encoding.UTF8.GetBytes(CapturedSave))).Allow);
        Assert.Equal("request body unreadable", t.Refused);
        var r = new JournalTraffic();
        Assert.False(r.Request("POST", RenderUrl, () => ("multipart/form-data", Encoding.UTF8.GetBytes("editor_type=quick"))).Allow);
        Assert.True(r.Request("POST", DialogUrl, () => ("application/json", Encoding.UTF8.GetBytes("{}"))).Allow);
        Assert.Null(Assert.Single(r.DialogRequests));
    }

    [Theory]
    [InlineData("2026-11-08", 2026, 11, 8)]
    [InlineData("2026-11-08T00:00:00.000Z", 2026, 11, 8)]
    [InlineData("Nov 8, 2026", 2026, 11, 8)]
    public void Dates_read_in_the_forms_the_site_uses(string s, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), JournalRequest.ParseDate(s));
    }

    [Theory]
    [InlineData("")]
    [InlineData("08/11/2026")]
    [InlineData("2026-11-08x")]
    [InlineData("Nov 8")]
    public void Other_dates_are_unreadable(string s)
    {
        Assert.Null(JournalRequest.ParseDate(s));
    }

    [Theory]
    [InlineData("Nov 8", "2026-11-08", "2026-11-08")]
    [InlineData("November 8", "2026-11-08", "2026-11-08")]
    [InlineData("Jan 2", "2026-12-30", "2027-01-02")]
    [InlineData("Dec 31", "2027-01-02", "2026-12-31")]
    [InlineData("Oct 3", "2026-11-08", "2026-10-03")]
    [InlineData("2026-11-09", "2026-11-08", "2026-11-09")]
    public void A_day_with_no_year_takes_the_jobs_year_across_new_year(string s, string anchor, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), JournalRequest.ParseDay(s, DateOnly.Parse(anchor)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Nov")]
    [InlineData("Feb 30")]
    public void A_day_that_isnt_one_is_unreadable(string s)
    {
        Assert.Null(JournalRequest.ParseDay(s, Day));
    }

    // The session dialog's request

    static List<KeyValuePair<string, string>> Form(params (string K, string V)[] fields) => fields.Select(f => new KeyValuePair<string, string>(f.K, f.V)).ToList();

    static List<KeyValuePair<string, string>> Dialog(string start = "Nov 8", string end = "Nov 8", string hours = "5", string minutes = "0", string id = "77") =>
        Form(("play_data[privacy]", ""), ("play_data[note]", ""), ("play_data[status]", ""), ("play_data[id]", id), ("play_data[hours]", hours),
            ("play_data[finish_date]", ""), ("play_data[start_date]", ""), ("play_data[minutes]", minutes), ("play_data[start]", start), ("play_data[end]", end),
            ("play_data[disableStart]", "false"), ("play_data[disableFinish]", "false"));

    [Fact]
    public void The_dialog_request_names_the_session_the_script_edits()
    {
        var (step, dialog) = JournalRequest.CheckDialog([Dialog()], Day, 300, "77");
        Assert.Null(step);
        Assert.Equal("77", dialog!.Id);
        Assert.Equal("Nov 8", dialog.Values["end"]);
        Assert.Equal("0", dialog.Values["minutes"]);
    }

    [Theory]
    [InlineData("0", "")]
    [InlineData("", "0")]
    [InlineData("", "")]
    public void Empty_hours_or_minutes_count_as_zero_on_both_sides(string hours, string minutes)
    {
        Assert.Null(JournalRequest.CheckDialog([Dialog(hours: hours, minutes: minutes)], Day, 0).Step);
        Assert.Null(JournalRequest.CheckDialog([Dialog(minutes: minutes)], Day, 300).Step);
    }

    [Fact]
    public void A_dialog_for_another_day_another_time_another_id_or_twice_is_refused()
    {
        Assert.Contains("day 2026-11-07", JournalRequest.CheckDialog([Dialog(start: "Nov 7")], Day, 300).Step);
        Assert.Contains("end", JournalRequest.CheckDialog([Dialog(end: "Nov 7")], Day, 300).Step);
        Assert.Contains("time", JournalRequest.CheckDialog([Dialog(hours: "4")], Day, 300).Step);
        Assert.Contains("count 2", JournalRequest.CheckDialog([Dialog(), Dialog()], Day, 300).Step);
        Assert.Contains("count 0", JournalRequest.CheckDialog([], Day, 300).Step);
        Assert.Contains("id", JournalRequest.CheckDialog([Dialog(id: "")], Day, 300).Step);
        Assert.Contains("on the page", JournalRequest.CheckDialog([Dialog()], Day, 300, "78").Step);
        Assert.Contains("unreadable", JournalRequest.CheckDialog([Dialog(start: "soon")], Day, 300).Step);
        Assert.Contains("body", JournalRequest.CheckDialog([null], Day, 300).Step);
    }

    [Theory]
    [InlineData("""{"pressed":true}""", true, "save")]
    [InlineData("""{"pressed":false,"step":"guard"}""", false, "guard")]
    [InlineData("null", false, "commit missing")]
    [InlineData("\"odd\"", true, "commit answer")]
    [InlineData("", true, "commit answer")]
    public void The_commit_answer_counts_anything_unclear_as_pressed(string json, bool pressed, string step)
    {
        Assert.Equal(new JournalCommitResult(pressed, step), JournalCommitResult.Parse(json));
    }

    // The traffic filter

    const string SaveUrl = "https://backloggd.com/api/user/1/log/4242";
    const string DialogUrl = "https://backloggd.com/open-date-modal/";
    const string RenderUrl = "https://backloggd.com/render-editor/4242";
    const string DataUrl = "https://backloggd.com/log/edit/4242?is_native=false";
    const string FormType = "application/x-www-form-urlencoded; charset=UTF-8";

    static Func<(string?, byte[]?)> Raw(string body) => () => (FormType, Encoding.UTF8.GetBytes(body));

    static readonly Func<(string?, byte[]?)> NoBody = () => throw new InvalidOperationException("body read");

    static JournalTraffic Armed()
    {
        var t = new JournalTraffic();
        t.Arm(Expect());
        return t;
    }

    [Theory]
    [InlineData("GET", "https://backloggd.com/games/hades/")]
    [InlineData("GET", "https://cdn.example.com/a.js")]
    [InlineData("HEAD", "https://backloggd.com/")]
    [InlineData("OPTIONS", "https://api.backloggd.com/x")]
    public void Reads_go_through_without_their_body_being_read(string method, string url)
    {
        Assert.True(new JournalTraffic().Request(method, url, NoBody).Allow);
    }

    [Theory]
    [InlineData("POST", "https://www.google-analytics.com/g/collect")]
    [InlineData("POST", "https://api.backloggd.com/log")]
    [InlineData("POST", "http://backloggd.com/api/user/1/log/4242")]
    [InlineData("PUT", "https://backloggd.com/api/user/1/playthrough/9")]
    [InlineData("DELETE", "https://backloggd.com/api/user/1/log/4242")]
    [InlineData("POST", "https://backloggd.com/users/sign_out")]
    [InlineData("POST", "https://backloggd.com/render-editor/4242/x")]
    public void Writes_to_any_other_host_or_path_never_go_out(string method, string url)
    {
        foreach (var t in new[] { new JournalTraffic(), Armed() })
        {
            var (allow, why) = t.Request(method, url, Raw(CapturedSave));
            Assert.False(allow);
            Assert.NotNull(why);
        }
    }

    [Fact]
    public void The_log_box_editor_request_goes_out_before_save_with_its_one_body()
    {
        var t = new JournalTraffic();
        Assert.True(t.Request("POST", RenderUrl, Raw("editor_type=quick")).Allow);
        Assert.True(t.Request("POST", RenderUrl, Raw("editor_type=quick")).Allow);
        Assert.Equal(["4242", "4242"], t.RenderedGameIds);
        Assert.False(t.Request("POST", RenderUrl, Raw("editor_type=full")).Allow);
        Assert.False(t.Request("POST", RenderUrl, Raw("editor_type=quick&x=1")).Allow);
        Assert.False(t.Request("POST", RenderUrl, () => ("application/json", Encoding.UTF8.GetBytes("editor_type=quick"))).Allow);
        Assert.False(t.Request("POST", "https://backloggd.com/render-editor/1", Raw("editor_type=quick")).Allow);
        Assert.Null(t.Refused);
        t.Arm(Expect());
        Assert.False(t.Request("POST", RenderUrl, Raw("editor_type=quick")).Allow);
    }

    [Fact]
    public void The_log_box_must_be_rendered_and_loaded_for_this_game()
    {
        var t = new JournalTraffic();
        Assert.Contains("request not seen", t.CheckLogBox("4242"));
        t.Request("POST", RenderUrl, Raw("editor_type=quick"));
        Assert.Contains("data not seen", t.CheckLogBox("4242"));
        t.Request("GET", DataUrl, NoBody);
        Assert.Equal("log data not read", t.CheckLogBox("4242"));
        t.LogDataRead = true;
        Assert.Equal("log data unreadable", t.CheckLogBox("4242"));
        t.LogData = JournalLogData.Parse(LoadedData);
        Assert.Null(t.CheckLogBox("4242"));
        Assert.Contains("for game 4242", t.CheckLogBox("1"));
        t.Request("GET", "https://backloggd.com/log/edit/7", NoBody);
        Assert.Contains("log data for game 7", t.CheckLogBox("4242"));
    }

    [Fact]
    public void The_dialog_request_goes_out_before_save_and_is_recorded()
    {
        var t = new JournalTraffic();
        Assert.True(t.Request("POST", DialogUrl, Raw("play_data%5Bid%5D=77")).Allow);
        Assert.Equal("77", Assert.Single(t.DialogRequests)!.Single().Value);
        t.Arm(Expect());
        Assert.False(t.Request("POST", DialogUrl, Raw("play_data%5Bid%5D=77")).Allow);
        Assert.Single(t.DialogRequests);
    }

    [Fact]
    public void A_save_tried_before_the_press_is_refused_and_ends_the_run()
    {
        var t = new JournalTraffic();
        Assert.False(t.Request("POST", SaveUrl, Raw(CapturedSave)).Allow);
        Assert.Equal("save before Save was pressed", t.Refused);
        t.Arm(Expect());
        Assert.False(t.Request("POST", SaveUrl, Raw(CapturedSave)).Allow);
        Assert.False(t.SaveSent);
    }

    [Fact]
    public void The_checked_save_goes_out_once()
    {
        var t = Armed();
        Assert.True(t.Request("POST", SaveUrl, Raw(CapturedSave)).Allow);
        Assert.True(t.SaveSent);
        Assert.Null(t.Refused);
        Assert.False(t.Request("POST", SaveUrl, Raw(CapturedSave)).Allow);
        Assert.Equal("a second save", t.Refused);
    }

    [Fact]
    public void A_refused_save_logs_its_key_names_but_never_its_values()
    {
        var t = Armed();
        Assert.False(t.Request("POST", SaveUrl, Raw(CapturedSave + "&log%5Bsecret_key%5D=hunter2")).Allow);
        Assert.Contains("unknown log key log[secret_key]", t.Refused);
        var keys = Assert.Single(t.Notes, n => n.StartsWith("save keys: "));
        Assert.Contains("log[secret_key]", keys);
        Assert.Contains("playthroughs[0][review]", keys);
        Assert.DoesNotContain("hunter2", string.Join("\n", t.Notes));
        Assert.DoesNotContain("605385", string.Join("\n", t.Notes));
    }

    [Fact]
    public void After_a_refused_save_a_later_one_that_would_pass_is_refused_too()
    {
        var t = Armed();
        Assert.False(t.Request("POST", SaveUrl, Raw(CapturedSave.Replace("%5Bhours%5D=6", "%5Bhours%5D=9"))).Allow);
        Assert.Contains("time", t.Refused);
        Assert.False(t.Request("POST", SaveUrl, Raw(CapturedSave)).Allow);
        Assert.False(t.SaveSent);
        Assert.False(t.Request("POST", DialogUrl, Raw("")).Allow);
    }

    [Fact]
    public void Nothing_goes_out_once_the_run_is_over()
    {
        var t = Armed();
        t.Closed = true;
        Assert.False(t.Request("POST", SaveUrl, Raw(CapturedSave)).Allow);
        Assert.False(t.SaveSent);
        Assert.True(t.Request("GET", "https://backloggd.com/", NoBody).Allow);
    }

    [Fact]
    public void A_save_for_another_game_or_with_no_body_is_refused()
    {
        Assert.False(Armed().Request("POST", "https://backloggd.com/api/user/1/log/1", Raw(CapturedSave)).Allow);
        var t = Armed();
        Assert.False(t.Request("PATCH", SaveUrl, () => (FormType, null)).Allow);
        Assert.Equal("request body unreadable", t.Refused);
    }

    [Theory]
    [InlineData(SaveUrl + "?deleted_dates%5B%5D=77")]
    [InlineData(SaveUrl + "?log%5Brating%5D=10")]
    [InlineData(SaveUrl + "?")]
    [InlineData(SaveUrl + "/?x=1")]
    public void A_save_with_any_query_is_refused_even_with_a_perfect_body(string url)
    {
        var t = Armed();
        Assert.False(t.Request("POST", url, Raw(CapturedSave)).Allow);
        Assert.Equal("save with a query", t.Refused);
        Assert.False(t.SaveSent);
        Assert.False(t.Request("POST", SaveUrl, Raw(CapturedSave)).Allow);
    }

    [Fact]
    public void The_log_box_and_dialog_requests_carry_no_query()
    {
        var t = new JournalTraffic();
        Assert.False(t.Request("POST", RenderUrl + "?editor_type=full", Raw("editor_type=quick")).Allow);
        Assert.False(t.Request("POST", DialogUrl + "?play_data%5Bid%5D=1", Raw("play_data%5Bid%5D=77")).Allow);
        Assert.Empty(t.RenderedGameIds);
        Assert.Empty(t.DialogRequests);
        Assert.Null(t.Refused);
        Assert.True(t.Request("GET", DataUrl, NoBody).Allow);
        Assert.Equal(["4242"], t.LogDataGameIds);
    }

    [Theory]
    [InlineData("X-HTTP-Method-Override")]
    [InlineData("X-HTTP-Method")]
    [InlineData("X-Method-Override")]
    public void A_method_override_header_is_refused_on_any_request(string header)
    {
        Func<string, bool> has = name => string.Equals(name, header, StringComparison.OrdinalIgnoreCase);
        var t = Armed();
        Assert.False(t.Request("POST", SaveUrl, Raw(CapturedSave), has).Allow);
        Assert.Equal($"method override header {header}", t.Refused);
        Assert.False(t.SaveSent);
        var before = new JournalTraffic();
        Assert.False(before.Request("POST", RenderUrl, Raw("editor_type=quick"), has).Allow);
        Assert.False(before.Request("POST", DialogUrl, Raw("play_data%5Bid%5D=77"), has).Allow);
        Assert.False(before.Request("GET", DataUrl, NoBody, has).Allow);
        Assert.Empty(before.RenderedGameIds);
        Assert.Null(before.Refused);
        Assert.True(Armed().Request("POST", SaveUrl, Raw(CapturedSave), _ => false).Allow);
    }

    [Fact]
    public void A_sign_in_page_after_the_press_is_noticed()
    {
        var t = new JournalTraffic();
        t.Request("GET", "https://backloggd.com/users/sign_in", NoBody);
        Assert.False(t.SignInSeen);
        t.Arm(Expect());
        t.Request("GET", "https://backloggd.com/users/sign_in?redirect=1", NoBody);
        Assert.True(t.SignInSeen);
    }

    // A new session for an empty day

    static readonly DateOnly Nov9 = new(2026, 11, 9);

    // The dialog request for the session the page made by a select on Nov 9: its negative id, the day
    // as start and last day, status 5, no time.
    static readonly JournalDialog Made = new("-2", new Dictionary<string, string?>
    {
        ["privacy"] = "", ["note"] = "", ["status"] = "5", ["id"] = "-2", ["hours"] = "", ["finish_date"] = "", ["start_date"] = "",
        ["minutes"] = "", ["start"] = "Nov 9", ["end"] = "Nov 9", ["disableStart"] = "false", ["disableFinish"] = "false",
    });

    static JournalExpect NewExpect(JournalDialog? dialog = null, string data = LoadedData, string? playthrough = "901") =>
        new("4242", Nov9, 0, 95, dialog ?? Made, false, JournalLogData.Parse(data), Cover, playthrough);

    // The captured save, but with the new session's entry for Nov 9 (1h 35m) in place of the Nov 8 one.
    static List<KeyValuePair<string, string>> NewSave() => Captured().Select(x => x.Key switch
    {
        "dates[901][0][id]" => new KeyValuePair<string, string>(x.Key, "-2"),
        "dates[901][0][range_start_date]" => new KeyValuePair<string, string>(x.Key, "2026-11-09"),
        "dates[901][0][range_end_date]" => new KeyValuePair<string, string>(x.Key, "2026-11-10"),
        "dates[901][0][hours]" => new KeyValuePair<string, string>(x.Key, "1"),
        _ => x,
    }).ToList();

    static List<KeyValuePair<string, string>> NewWith(string key, string value) =>
        NewSave().Select(x => x.Key == key ? new KeyValuePair<string, string>(key, value) : x).ToList();

    [Fact]
    public void A_new_session_saved_as_the_page_makes_it_goes_out()
    {
        Assert.Null(JournalRequest.CheckSave(NewSave(), NewExpect()));
    }

    [Theory]
    [InlineData("dates[901][0][hours]", "2", "time")]
    [InlineData("dates[901][0][range_end_date]", "2026-11-11", "range_end_date")]
    [InlineData("dates[901][0][range_start_date]", "2026-11-08", "day")]
    [InlineData("dates[901][0][id]", "-3", "session id")]
    [InlineData("dates[901][0][status]", "1", "status")]
    [InlineData("dates[901][0][edited]", "false", "not edited")]
    [InlineData("playthroughs[0][hours_played]", "14", "expected 720m or 815m")]
    public void A_new_session_save_that_differs_is_refused(string key, string value, string why)
    {
        Assert.Contains(why, JournalRequest.CheckSave(NewWith(key, value), NewExpect()));
    }

    [Theory]
    [InlineData("note", "Boss")]
    [InlineData("privacy", "1")]
    [InlineData("start_date", "2026-11-09")]
    [InlineData("finish_date", "2026-11-09")]
    public void A_new_session_carries_nothing_but_its_day_and_time(string field, string value)
    {
        // Even when the dialog request agreed, a new session may not carry it.
        var dialog = new JournalDialog("-2", new Dictionary<string, string?>(Made.Values) { [field] = value });
        Assert.Equal($"request dates: new session {field}", JournalRequest.CheckSave(NewWith($"dates[901][0][{field}]", value), NewExpect(dialog)));
    }

    [Fact]
    public void A_new_session_with_tags_is_refused()
    {
        var f = NewSave();
        f.Add(new("dates[901][0][tags][]", "boss"));
        Assert.Equal("request dates: new session tags", JournalRequest.CheckSave(f, NewExpect()));
    }

    [Fact]
    public void A_new_session_on_a_day_the_log_data_already_has_is_refused()
    {
        var onNov8 = new JournalExpect("4242", Day, 0, 95, Made with { Values = new Dictionary<string, string?>(Made.Values) { ["start"] = "Nov 8", ["end"] = "Nov 8" } },
            false, JournalLogData.Parse(LoadedData), Cover, "901");
        var f = NewSave().Select(x => x.Key switch
        {
            "dates[901][0][range_start_date]" => new KeyValuePair<string, string>(x.Key, "2026-11-08"),
            "dates[901][0][range_end_date]" => new KeyValuePair<string, string>(x.Key, "2026-11-09"),
            _ => x,
        }).ToList();
        Assert.Equal("request dates: the day already has a session in the log data", JournalRequest.CheckSave(f, onNov8));
    }

    [Fact]
    public void A_new_session_must_sit_under_the_playthrough_it_was_made_in()
    {
        Assert.Contains("new session playthrough 902 not in the log data", JournalRequest.CheckSave(NewSave(), NewExpect(playthrough: "902")));
        var moved = NewSave().Select(x => x.Key.StartsWith("dates[901]") ? new KeyValuePair<string, string>(x.Key.Replace("dates[901]", "dates[902]"), x.Value) : x).ToList();
        Assert.Contains("playthrough 902 not in the log data", JournalRequest.CheckSave(moved, NewExpect()));
    }

    [Fact]
    public void A_saved_session_id_is_never_a_new_one_and_a_new_one_is_never_saved()
    {
        Assert.Contains("new session id 77", JournalRequest.CheckSave(NewWith("dates[901][0][id]", "77"), NewExpect(Made with { Id = "77" })));
        var negative = new JournalDialog("-2", Session.Values);
        Assert.Equal("request dates: session id negative", Check(With("dates[901][0][id]", "-2"), Expect() with { Dialog = negative }));
    }

    [Theory]
    [InlineData("""[{"id":5,"range_start_date":"2026-11-08","range_end_date":"2026-11-09"}]""", null)]
    [InlineData("""[{"id":5,"range_start_date":"2026-11-09","range_end_date":"2026-11-10"}]""", "already has a session")]
    [InlineData("""[{"id":5,"range_start_date":"2026-11-07","range_end_date":"2026-11-11"}]""", "already has a session")]
    [InlineData("""[{"id":5,"range_start_date":"2026-11-09","range_end_date":null}]""", "already has a session")]
    [InlineData("""[{"id":5,"range_start_date":"2026-11-10","range_end_date":"2026-11-11"}]""", null)]
    [InlineData("""[{"id":5,"range_start_date":null,"range_end_date":"2026-11-10"}]""", "session days unreadable")]
    [InlineData("""[{"id":5,"range_start_date":"soon","range_end_date":"2026-11-10"}]""", "session days unreadable")]
    public void The_log_datas_sessions_decide_whether_a_day_is_empty(string playDates, string? why)
    {
        var data = JournalLogData.Parse("{\"game_log\":{\"status\":\"completed\"},\"playthroughs\":{\"901\":{\"id\":901,\"play_dates\":" + playDates + "}}}");
        var step = JournalRequest.CheckNewSession(data, Nov9, "901", "-1");
        if (why is null) Assert.Null(step);
        else Assert.Contains(why, step);
    }

    [Theory]
    [InlineData("-1", "901", null)]
    [InlineData("1", "901", "new session id")]
    [InlineData("-0", "901", "new session id")]
    [InlineData(null, "901", "new session id")]
    [InlineData("-1", null, "playthrough unreadable")]
    [InlineData("-1", "abc", "playthrough unreadable")]
    [InlineData("-1", "", "playthrough unreadable")]
    [InlineData("-1", "902", "not in the log data")]
    public void A_new_session_needs_a_page_made_id_and_a_known_playthrough(string? id, string? playthrough, string? why)
    {
        var step = JournalRequest.CheckNewSession(JournalLogData.Parse(LoadedData), Nov9, playthrough, id);
        if (why is null) Assert.Null(step);
        else Assert.Contains(why, step);
        Assert.Equal("log data unreadable", JournalRequest.CheckNewSession(null, Nov9, "901", "-1"));
    }

    [Fact]
    public void A_new_session_save_goes_out_through_the_traffic_check()
    {
        var t = new JournalTraffic();
        t.Arm(NewExpect());
        var body = string.Join("&", NewSave().Select(f => Uri.EscapeDataString(f.Key) + "=" + Uri.EscapeDataString(f.Value)));
        Assert.True(t.Request("POST", SaveUrl, Raw(body)).Allow);
        Assert.True(t.SaveSent);
    }

    // Sync sessions turned on by the run

    // The log data with Sync sessions off for playthrough 901 (its one session holds 5h).
    static readonly string SyncOff = LoadedData.Replace("\"sync_sessions\":true", "\"sync_sessions\":false");

    static JournalExpect SyncExpect(bool turnedOn = true) => Expect(data: SyncOff) with { SyncTurnedOn = turnedOn };

    static List<KeyValuePair<string, string>> SyncSave(string hours, string minutes) => Captured().Select(x => x.Key switch
    {
        "playthroughs[0][hours_played]" => new KeyValuePair<string, string>(x.Key, hours),
        "playthroughs[0][mins_played]" => new KeyValuePair<string, string>(x.Key, minutes),
        _ => x,
    }).ToList();

    [Theory]
    [InlineData("12", "0")]   // the page left Time Played as loaded; the site works it out
    [InlineData("12", "")]    // the same amount with an empty field (live: a new log sent both empty for 0/0)
    [InlineData("6", "35")]   // the sum of the sessions (5h) plus the 95m added
    [InlineData("6", "35.3")]
    public void Sync_sessions_turned_on_takes_time_played_as_loaded_or_the_sum_of_sessions(string hours, string minutes)
    {
        Assert.Null(Check(SyncSave(hours, minutes), SyncExpect()));
    }

    [Fact]
    public void Sync_sessions_turned_on_refuses_any_other_time_played_naming_the_values()
    {
        Assert.Equal("request playthroughs[0]: hours_played 13/35 sent, 12/0 loaded, sessions 300m, expected 395m", Check(SyncSave("13", "35"), SyncExpect()));
        Assert.Equal("request playthroughs[0]: hours_played 6/x sent, 12/0 loaded, sessions 300m, unreadable", Check(SyncSave("6", "x"), SyncExpect()));
    }

    [Fact]
    public void Sync_sessions_may_go_from_off_to_on_only_when_the_run_ticked_it()
    {
        // Ticked by the run: off in the data, on in the save.
        Assert.Null(Check(SyncSave("6", "35"), SyncExpect()));
        // Not reported: the change is refused like any other.
        Assert.Contains("sync_sessions]: differs from the log data", Check(SyncSave("12", "0"), SyncExpect(turnedOn: false)));
        // Reported, but the save keeps it off.
        Assert.Contains("[sync_sessions]: false sent, false loaded", Check(With("playthroughs[0][sync_sessions]", "false"), SyncExpect()));
        // Reported, but it was already on in the data.
        Assert.Contains("[sync_sessions]: true sent, true loaded", Check(Captured(), Expect() with { SyncTurnedOn = true }));
    }

    [Theory]
    [InlineData(false, "901", "901", true, null)]
    [InlineData(true, "901", "901", false, null)]
    [InlineData(false, "901", "901", false, "off and not turned on")]
    [InlineData(true, "901", "901", true, "on in the log data")]
    [InlineData(false, "901", "902", true, "sync box is playthrough 902's")]
    [InlineData(false, "903", "903", true, "not in the log data")]
    public void Sync_sessions_ends_up_on_for_the_sessions_playthrough(bool loadedOn, string session, string current, bool turnedOn, string? why)
    {
        var data = JournalLogData.Parse(loadedOn ? LoadedData : SyncOff);
        var step = JournalRequest.CheckSync(data, session, current, turnedOn);
        if (why is null) Assert.Null(step);
        else Assert.Contains(why, step);
    }

    [Fact]
    public void Session_sums_add_up_every_session_of_the_playthrough()
    {
        var data = JournalLogData.Parse(LoadedData.Replace("""[{"id":77,""", """[{"id":76,"hours":1,"minutes":30,"range_start_date":"2026-11-01","range_end_date":"2026-11-02"},{"id":77,"""))!;
        Assert.Equal(390m, data.SessionSum("901"));
        Assert.Equal(0m, data.SessionSum("999"));
        Assert.Null(JournalLogData.Parse(LoadedData.Replace("\"hours\":5", "\"hours\":\"five\""))!.SessionSum("901"));
    }

    // A session from an earlier day that covers the day

    // Session 77 runs Nov 7 to Nov 8 (range_end_date Nov 9, exclusive); the day is Nov 8.
    static readonly string Spanning = LoadedData.Replace("\"range_start_date\":\"2026-11-08\"", "\"range_start_date\":\"2026-11-07\"");

    static readonly JournalDialog SpanDialog = new("77", new Dictionary<string, string?>(Session.Values) { ["start"] = "Nov 7", ["end"] = "Nov 8" });

    static List<KeyValuePair<string, string>> SpanSave() => With("dates[901][0][range_start_date]", "2026-11-07");

    [Fact]
    public void A_session_from_an_earlier_day_covering_the_day_takes_the_time()
    {
        Assert.Null(Check(SpanSave(), Expect(data: Spanning) with { Dialog = SpanDialog, Continued = true }));
        // Without the continued mode the start must be the day itself.
        Assert.Equal("request dates: day", Check(SpanSave(), Expect(data: Spanning) with { Dialog = SpanDialog }));
        // Its start must stay its own.
        Assert.Equal("request dates: day", Check(Captured(), Expect(data: Spanning) with { Dialog = SpanDialog, Continued = true }));
    }

    [Fact]
    public void A_continuation_must_cover_the_day_in_the_log_data()
    {
        var shortOne = Spanning.Replace("\"range_end_date\":\"2026-11-09\"", "\"range_end_date\":\"2026-11-08\"");
        Assert.Equal("request dates: session 77 doesn't cover the day in the log data", Check(SpanSave(), Expect(data: shortOne) with { Dialog = SpanDialog, Continued = true }));
        Assert.Contains("doesn't cover the day", JournalRequest.CheckCovering(JournalLogData.Parse(shortOne), Day, "77"));
        Assert.Null(JournalRequest.CheckCovering(JournalLogData.Parse(Spanning), Day, "77"));
        Assert.Contains("doesn't cover the day", JournalRequest.CheckCovering(JournalLogData.Parse(LoadedData), Day, "77"));
        Assert.Contains("not in the log data", JournalRequest.CheckCovering(JournalLogData.Parse(LoadedData), Day, "78"));
        Assert.Contains("not in the log data", JournalRequest.CheckCovering(null, Day, "77"));
    }

    [Fact]
    public void The_dialog_request_of_a_continuation_starts_earlier_and_reaches_the_day()
    {
        static List<KeyValuePair<string, string>> D(string start, string end) =>
            Form(("play_data[id]", "77"), ("play_data[start]", start), ("play_data[end]", end), ("play_data[hours]", "5"), ("play_data[minutes]", "0"));
        Assert.Null(JournalRequest.CheckDialog([D("Nov 7", "Nov 8")], Day, 300, "77", covering: true).Step);
        Assert.Null(JournalRequest.CheckDialog([D("Nov 6", "Nov 10")], Day, 300, "77", covering: true).Step);
        Assert.Contains("session request day 2026-11-08", JournalRequest.CheckDialog([D("Nov 8", "Nov 8")], Day, 300, "77", covering: true).Step);
        Assert.Contains("doesn't reach the day", JournalRequest.CheckDialog([D("Nov 6", "Nov 7")], Day, 300, "77", covering: true).Step);
        Assert.Contains("session request day 2026-11-07", JournalRequest.CheckDialog([D("Nov 7", "Nov 8")], Day, 300, "77").Step);
    }
}

using System.Text;
using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

// Mark as on a game with no log: the one save the app makes that creates a log.
public class NewLogSaveTests
{
    const string NoLogData = """{"playthroughs":{},"most_recent_playthrough_id":null}""";

    static readonly StatusPick Shelved = new(PlayedStatus.Shelved, null, null, null);

    // The Create Log body captured live on 2026-11-11 (Shelved picked), game id anonymized.
    const string CreateLog =
        "game_id=4242" +
        "&playthroughs%5B0%5D%5Bid%5D=-1&playthroughs%5B0%5D%5Btitle%5D=Log&playthroughs%5B0%5D%5Brating%5D=NaN&playthroughs%5B0%5D%5Bsync_sessions%5D=false" +
        "&playthroughs%5B0%5D%5Breview%5D=&playthroughs%5B0%5D%5Breview_spoilers%5D=false&playthroughs%5B0%5D%5Bplatform%5D=&playthroughs%5B0%5D%5Bhours_played%5D=" +
        "&playthroughs%5B0%5D%5Bmins_played%5D=&playthroughs%5B0%5D%5Bis_master%5D=false&playthroughs%5B0%5D%5Bis_replay%5D=false&playthroughs%5B0%5D%5Bstart_date%5D=" +
        "&playthroughs%5B0%5D%5Bfinish_date%5D=&playthroughs%5B0%5D%5Bedition_id%5D=&playthroughs%5B0%5D%5Bedition_type%5D=&playthroughs%5B0%5D%5Bmedium_id%5D=" +
        "&playthroughs%5B0%5D%5Bplayed_platform%5D=&playthroughs%5B0%5D%5Bstorefront_id%5D=" +
        "&log%5Bgame_liked%5D=false&log%5Bid%5D=&log%5Blast_edited_at%5D=&log%5Boverride_cover_id%5D=185486&log%5Bis_play%5D=true" +
        "&log%5Bis_playing%5D=false&log%5Bis_backlog%5D=false&log%5Bis_wishlist%5D=false&log%5Bstatus%5D=shelved" +
        "&modal_type=quick";

    static List<KeyValuePair<string, string>> Created(params (string Key, string? Value)[] changes)
    {
        var f = JournalRequest.ParseBody("application/x-www-form-urlencoded", Encoding.UTF8.GetBytes(CreateLog))!;
        foreach (var (k, v) in changes)
        {
            var i = f.FindIndex(x => x.Key == k);
            if (v is null) f.RemoveAt(i);
            else if (i >= 0) f[i] = new(k, v);
            else f.Add(new(k, v));
        }
        return f;
    }

    static StatusExpect Expect(StatusPick? pick = null) =>
        new("4242", pick ?? Shelved, JournalLogData.Parse(NoLogData), "185486", NewLog: true, LoadedStatus: "completed");

    static string? Check(List<KeyValuePair<string, string>> f, StatusExpect? x = null) => JournalRequest.CheckStatusSave(f, x ?? Expect());

    [Fact]
    public void Log_data_for_a_game_with_no_log_reads_as_none()
    {
        var data = JournalLogData.Parse(NoLogData)!;
        Assert.True(data.NoLog);
        Assert.Empty(data.Playthroughs);
        Assert.False(JournalLogData.Parse(JournalRequestTests.LoadedData)!.NoLog);
        Assert.Null(JournalLogData.Parse("""{"playthroughs":{},"most_recent_playthrough_id":5}"""));
        Assert.Null(JournalLogData.Parse("""{"playthroughs":{},"most_recent_playthrough_id":null,"other":1}"""));
        Assert.Null(JournalLogData.Parse("""{"playthroughs":{"1":{"id":1}},"most_recent_playthrough_id":null}"""));
    }

    [Fact]
    public void The_captured_create_log_save_goes_out()
    {
        Assert.Null(Check(Created()));
        // No rating may also be sent as 0; no checked cover sends an empty one.
        Assert.Null(Check(Created(("playthroughs[0][rating]", "0"))));
        Assert.Null(Check(Created(("log[override_cover_id]", "")), Expect() with { CoverId = null }));
    }

    [Theory]
    [InlineData("playthroughs[0][rating]", "8")]
    [InlineData("playthroughs[0][review]", "Great")]
    [InlineData("playthroughs[0][id]", "5")]
    [InlineData("playthroughs[0][title]", "Replay")]
    [InlineData("playthroughs[0][hours_played]", "3")]
    [InlineData("playthroughs[0][sync_sessions]", "true")]
    [InlineData("log[id]", "118795381")]
    [InlineData("log[last_edited_at]", "1794203781")]
    [InlineData("log[game_liked]", "true")]
    [InlineData("log[override_cover_id]", "1")]
    [InlineData("log[status]", "completed")]
    [InlineData("log[is_play]", "false")]
    [InlineData("log[is_playing]", "true")]
    [InlineData("log[is_wishlist]", "true")]
    public void Any_other_value_in_a_new_log_is_refused_by_name(string key, string value)
    {
        Assert.StartsWith($"new log: {key} ", Check(Created((key, value))));
    }

    [Fact]
    public void A_new_log_for_another_game_or_editor_is_refused()
    {
        Assert.Equal("request game_id", Check(Created(("game_id", "1"))));
        Assert.Equal("request modal_type full", Check(Created(("modal_type", "full"))));
    }

    [Theory]
    [InlineData("dates[-1][0][hours]", "1")]
    [InlineData("deleted_dates", "")]
    [InlineData("playthroughs[0][created_at]", "")]
    [InlineData("playthroughs[1][id]", "-2")]
    [InlineData("log[rating]", "0")]
    [InlineData("_method", "patch")]
    public void An_extra_key_in_a_new_log_is_refused(string key, string value)
    {
        Assert.Equal($"new log: unknown key {key}", Check(Created((key, value))));
    }

    [Theory]
    [InlineData("playthroughs[0][storefront_id]")]
    [InlineData("log[last_edited_at]")]
    [InlineData("modal_type")]
    public void A_missing_key_in_a_new_log_is_refused(string key)
    {
        Assert.Equal($"new log: {key} missing", Check(Created((key, null))));
    }

    [Fact]
    public void Toggles_alone_on_a_new_log_keep_the_boxs_status_and_leave_played_off()
    {
        var backlog = new StatusPick(null, null, true, null);
        Assert.Null(Check(Created(("log[status]", "completed"), ("log[is_play]", "false"), ("log[is_backlog]", "true")), Expect(backlog)));
        Assert.StartsWith("new log: log[is_play] ", Check(Created(("log[status]", "completed"), ("log[is_backlog]", "true")), Expect(backlog)));
        Assert.Equal("new log: no status", Check(Created(), Expect(backlog) with { LoadedStatus = "" }));
    }

    [Fact]
    public void A_new_log_needs_the_box_and_the_log_data_to_agree()
    {
        var existing = JournalLogData.Parse(JournalRequestTests.LoadedData);
        Assert.Contains("disagree", Check(Created(), Expect() with { Data = existing }));
        Assert.Contains("disagree", Check(Created(), Expect() with { NewLog = false }));
    }

    [Fact]
    public void A_create_log_save_goes_out_once_through_the_traffic_filter()
    {
        const string url = "https://backloggd.com/api/user/1/log/4242";
        Func<(string?, byte[]?)> body = () => ("application/x-www-form-urlencoded; charset=UTF-8", Encoding.UTF8.GetBytes(CreateLog));
        var t = new JournalTraffic();
        t.ArmStatus(Expect());
        Assert.True(t.Request("POST", url, body).Allow);
        Assert.False(t.Request("POST", url, body).Allow);
        Assert.Equal("a second save", t.Refused);
    }

    [Fact]
    public void The_script_message_for_a_new_log_parses()
    {
        var r = JournalScriptResult.Parse("""{"type":"journal","ok":true,"statusReady":true,"gameId":"4242","log":{"status":"completed"},"after":{"status":"shelved"},"coverId":"185486","newLog":true,"playthroughId":"-1","logId":"","saveLabel":"Create Log"}""");
        Assert.Equal((true, "-1", "", "Create Log"), (r.NewLog, r.PlaythroughId, r.LogId, r.SaveLabel));
    }
}

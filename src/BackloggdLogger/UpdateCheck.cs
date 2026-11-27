using System.Net.Http;
using System.Text.Json;
using BackloggdLogger.Core;

namespace BackloggdLogger;

// Once per run, when the setting is on, no game is running and popups aren't paused, asks GitHub for the latest release
// and announces a newer one once per version.
sealed class UpdateCheck : IDisposable
{
    const string Api = "https://api.github.com/repos/ceeprus/backloggd-auto-import/releases/latest";

    readonly System.Windows.Forms.Timer _timer = new() { Interval = 30_000 };
    readonly AppState _state;
    readonly Func<bool> _busy;
    readonly Action<Version, Version, string> _notify;
    readonly Action _save;

    public UpdateCheck(AppState state, Func<bool> busy, Action<Version, Version, string> notify, Action save)
    {
        _state = state;
        _busy = busy;
        _notify = notify;
        _save = save;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public static Version Current => typeof(UpdateCheck).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public void Start() => _timer.Start();

    async Task TickAsync()
    {
        _timer.Interval = 60_000;
        if (_busy()) return;
        _timer.Stop();
        try
        {
            var (tag, url) = await LatestAsync();
            if (Releases.Newer(tag, Current) is not { } latest || _state.UpdateNotified == latest.ToString(3)) return;
            _state.UpdateNotified = latest.ToString(3);
            _save();
            _notify(latest, Current, Releases.SafeUrl(url));
        }
        catch (Exception ex)
        {
            Log.Write("update", "check failed: " + ex.Message);
            // Offline at boot, say: try again in an hour rather than never this run.
            _timer.Interval = 3_600_000;
            _timer.Start();
        }
    }

    // The user's own check from Settings: it runs whatever the setting, a running game or an
    // earlier announcement say. The newer version, or null when this one is the latest; throws
    // when GitHub can't be reached.
    public async Task<Version?> CheckNowAsync()
    {
        var (tag, url) = await LatestAsync();
        if (Releases.Newer(tag, Current) is not { } latest) return null;
        _state.UpdateNotified = latest.ToString(3);
        _save();
        _notify(latest, Current, Releases.SafeUrl(url));
        return latest;
    }

    static async Task<(string? Tag, string? Url)> LatestAsync()
    {
        var (tag, url, _) = await LatestReleaseAsync();
        return (tag, url);
    }

    internal static async Task<(string? Tag, string? Url, (string Url, string Sha256)? Exe)> LatestReleaseAsync()
    {
        using var http = Client(TimeSpan.FromSeconds(15));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var doc = JsonDocument.Parse(await http.GetStringAsync(Api));
        var root = doc.RootElement;
        return (root.TryGetProperty("tag_name", out var t) ? t.GetString() : null,
                root.TryGetProperty("html_url", out var u) ? u.GetString() : null,
                Releases.ExeAsset(root));
    }

    internal static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BackloggdLogger");
        return http;
    }

    public void Dispose() => _timer.Dispose();
}

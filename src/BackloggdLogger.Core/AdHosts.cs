namespace BackloggdLogger.Core;

// Ad and tracking hosts the app's Backloggd window answers with an empty 403. A host is blocked
// when it is a listed host or a subdomain of one; a host the log box needs never is.
public static class AdHosts
{
    public static IReadOnlyList<string> Blocked { get; } = new[]
    {
        "doubleclick.net",
        "googlesyndication.com",
        "googleadservices.com",
        "adservice.google.com",
        "google-analytics.com",
        "googletagservices.com",
        "googletagmanager.com",
        "adnxs.com",
        "amazon-adsystem.com",
        "criteo.com",
        "criteo.net",
        "pubmatic.com",
        "rubiconproject.com",
        "openx.net",
        "taboola.com",
        "outbrain.com",
        "media.net",
        "adsrvr.org",
        "casalemedia.com",
        "moatads.com",
        "scorecardresearch.com",
        "quantserve.com",
        "33across.com",
        "sharethrough.com",
        "teads.tv",
        "yieldmo.com",
        "smartadserver.com",
        "id5-sync.com",
        "rlcdn.com",
        "playwire.com",
        "ezoic.net",
        "venatusmedia.com",
        // Venatus' header-bidding host, the one Backloggd's pages load (hb.vntsm.com).
        "vntsm.com",
        "freestar.io",
        "nitropay.com",
        "carbonads.net",
    };

    // Backloggd, its CDN, the cover images and Cloudflare's challenge pages.
    public static IReadOnlyList<string> Needed { get; } = new[]
    {
        "backloggd.com",
        "backloggd.b-cdn.net",
        "igdb.com",
        "cloudflare.com",
    };

    public static bool IsBlocked(string? host)
    {
        host = host?.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(host)) return false;
        foreach (var d in Needed) if (Under(host, d)) return false;
        foreach (var d in Blocked) if (Under(host, d)) return true;
        return false;
    }

    public static bool IsBlockedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme is "http" or "https" or "ws" or "wss"
        && IsBlocked(u.Host);

    // WebView2 request filters: only requests to these reach the handler, which still checks the
    // host itself (a filter's * can also match inside a path).
    public static IEnumerable<string> FilterPatterns =>
        Blocked.SelectMany(h => new[] { $"*://{h}/*", $"*://*.{h}/*" });

    public static IReadOnlyList<string> SlotSelectors { get; } = new[]
    {
        "[id^=\"div-gpt-ad\"]",
        ".adsbygoogle",
        "[data-ad-slot]",
        "iframe[src*=\"googlesyndication\"]",
    };

    // Hides the slots blocked ads leave empty; nothing of Backloggd's own UI.
    public static string SlotCss => string.Join(", ", SlotSelectors) + " { display: none !important; }";

    static bool Under(string host, string domain) =>
        host == domain || (host.Length > domain.Length && host.EndsWith(domain, StringComparison.Ordinal) && host[host.Length - domain.Length - 1] == '.');
}

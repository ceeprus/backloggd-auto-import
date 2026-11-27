using System.Security.Cryptography;
using System.Text.Json;

namespace BackloggdLogger.Core;

public static class Releases
{
    public const string Page = "https://github.com/ceeprus/backloggd-auto-import/releases/latest";

    // The release's version when it is newer than current ("v1.4.0" or "1.4.0"); compared on three parts,
    // since the exe reports four (1.3.0.0).
    public static Version? Newer(string? tag, Version current)
    {
        if (!Version.TryParse(tag?.Trim().TrimStart('v', 'V'), out var v)) return null;
        var latest = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        return latest > new Version(current.Major, current.Minor, Math.Max(0, current.Build)) ? latest : null;
    }

    // Only this repo's own release pages are opened; anything else falls back to the latest release page.
    public static string SafeUrl(string? url) =>
        url is not null && url.StartsWith("https://github.com/ceeprus/backloggd-auto-import/releases/", StringComparison.Ordinal) ? url : Page;

    public const string ExeName = "BackloggdLogger.exe";

    const string DownloadPrefix = "https://github.com/ceeprus/backloggd-auto-import/releases/download/";

    // The release's exe and the SHA-256 GitHub reports for it. Null when either is missing or the link leaves this repo.
    public static (string Url, string Sha256)? ExeAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object || Text(a, "name") != ExeName) continue;
            var url = Text(a, "browser_download_url");
            var digest = Text(a, "digest");
            if (url is null || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) return null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71) return null;
            var hash = digest[7..].ToLowerInvariant();
            return hash.All(Uri.IsHexDigit) ? (url, hash) : null;
        }
        return null;
    }

    public static bool HashMatches(ReadOnlySpan<byte> data, string sha256) =>
        Convert.ToHexStringLower(SHA256.HashData(data)) == sha256;

    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

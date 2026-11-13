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
}

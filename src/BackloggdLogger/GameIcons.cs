using System.Text.RegularExpressions;
using BackloggdLogger.Core;
using Microsoft.Win32;

namespace BackloggdLogger;

// Row icons for Tracked games, loaded when a row is first drawn and kept per game id until the window closes.
// Anything that fails is null, and the row draws the placeholder.
sealed partial class GameIcons : IDisposable
{
    // The test harness points this at a Steam folder of its own; the app reads Steam's path from the registry.
    internal static string? SteamRootOverride { get; set; }

    readonly Dictionary<string, Image?> _cache = new();
    string? _steamRoot;
    bool _steamRead;

    [GeneratedRegex("^[0-9a-f]{40}\\.jpg$", RegexOptions.IgnoreCase)]
    private static partial Regex HashedIcon();

    // Helpers that ship next to a game's exe and are never the game itself.
    static readonly string[] NotTheGame = { "unins", "crash", "unitycrashhandler", "launcher", "setup", "vc_redist", "dxsetup", "ue4prereq" };

    public Image? For(GameRecord g)
    {
        if (_cache.TryGetValue(g.Id, out var image)) return image;
        try { image = Load(g); }
        catch (Exception) { image = null; }
        _cache[g.Id] = image;
        return image;
    }

    // An icon by exe path alone (the window picker has no game record yet), cached per path.
    public Image? ForExe(string exePath)
    {
        var key = "exe:" + exePath.ToLowerInvariant();
        if (_cache.TryGetValue(key, out var image)) return image;
        try { image = ExeIcon(exePath); }
        catch (Exception) { image = null; }
        _cache[key] = image;
        return image;
    }

    internal int Loaded => _cache.Count;

    static readonly Font InitialFont = new("Segoe UI Semibold", 8.25f);

    // The icon scaled to a pixel size once (bicubic) and kept, so a repaint is a straight copy of
    // pixels. Keyed by the source picture and the size; the copies go with the cache.
    readonly Dictionary<(Image Source, Size Size), Bitmap> _scaled = new();

    Bitmap Scaled(Image source, Size size)
    {
        if (_scaled.TryGetValue((source, size), out var bitmap)) return bitmap;
        bitmap = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(source, new Rectangle(Point.Empty, size));
        }
        _scaled[(source, size)] = bitmap;
        return bitmap;
    }

    internal int ScaledCount => _scaled.Count;

    // The icon scaled into r, or with no icon the name's first letter in text-subtle on a surface
    // tile (radius 4), so the row still has a picture and nothing that looks like a control.
    public void Draw(Graphics g, Rectangle r, Image? image, string? name, int radius)
    {
        var state = g.Save();
        if (image is not null && r.Width > 0 && r.Height > 0)
        {
            var scaled = Scaled(image, r.Size);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(scaled, r, 0, 0, scaled.Width, scaled.Height, GraphicsUnit.Pixel);
            g.Restore(state);
            return;
        }
        Theme.Crisp(g);
        using (var path = Theme.Round(r, radius))
        using (var brush = new SolidBrush(Theme.Surface))
            g.FillPath(brush, path);
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Default;
        var initial = name?.Trim() is { Length: > 0 } n ? char.ToUpper(n[0], System.Globalization.CultureInfo.CurrentCulture).ToString() : "";
        TextRenderer.DrawText(g, initial, InitialFont, r, Theme.TextSubtle, Theme.Surface,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        g.Restore(state);
    }

    Image? Load(GameRecord g)
    {
        if (GameIds.IsSteam(g.Id) && uint.TryParse(g.Id["steam:".Length..], out var appId))
            return SteamIcon(appId) ?? ExeIcon(SteamExe(appId));
        return ExeIcon(g.ExePath ?? g.EmulatorExe ?? g.LauncherExe);
    }

    string? SteamRoot()
    {
        if (_steamRead) return _steamRoot;
        _steamRead = true;
        if (SteamRootOverride is { } root) return _steamRoot = root;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            _steamRoot = key?.GetValue("SteamPath") as string;
        }
        catch (Exception) { _steamRoot = null; }
        return _steamRoot;
    }

    // Steam keeps a small icon per game in its library cache: <appid>_icon.jpg, or in newer clients
    // a file named by its hash inside the <appid> folder.
    Image? SteamIcon(uint appId)
    {
        if (SteamRoot() is not { } root) return null;
        var cache = Path.Combine(root, "appcache", "librarycache");
        var folder = Path.Combine(cache, appId.ToString());
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder, "*.jpg"))
                if (HashedIcon().IsMatch(Path.GetFileName(file)) && Picture(file) is { } hashed) return hashed;
        var old = Path.Combine(cache, $"{appId}_icon.jpg");
        return File.Exists(old) ? Picture(old) : null;
    }

    // The installed game's main exe: the largest exe in its install folder that isn't a helper.
    string? SteamExe(uint appId)
    {
        if (SteamRoot() is not { } root) return null;
        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        var libraries = File.Exists(vdf) ? SteamFiles.LibraryPaths(File.ReadAllText(vdf)) : new[] { root };
        foreach (var lib in libraries)
        {
            var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(acf) || SteamFiles.InstallDir(File.ReadAllText(acf)) is not { } dir) continue;
            var folder = Path.Combine(lib, "steamapps", "common", dir);
            if (!Directory.Exists(folder)) continue;
            return Directory.EnumerateFiles(folder, "*.exe")
                .Where(f => !NotTheGame.Any(n => Path.GetFileName(f).StartsWith(n, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }
        return null;
    }

    static Image? ExeIcon(string? exe)
    {
        if (exe is null || !File.Exists(exe)) return null;
        using var icon = Icon.ExtractAssociatedIcon(exe);
        return icon?.ToBitmap();
    }

    // A copy, so the file isn't held open while the window shows it.
    static Image? Picture(string file)
    {
        using var stream = File.OpenRead(file);
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    public void Dispose()
    {
        foreach (var bitmap in _scaled.Values) bitmap.Dispose();
        _scaled.Clear();
        foreach (var image in _cache.Values) image?.Dispose();
        _cache.Clear();
    }
}

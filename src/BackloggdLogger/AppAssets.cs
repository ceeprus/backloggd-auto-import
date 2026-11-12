using System.Drawing;
using System.Reflection;

namespace BackloggdLogger;

static class AppAssets
{
    static readonly Assembly Asm = typeof(AppAssets).Assembly;

    public static readonly Icon AppIcon = LoadIcon(null);
    public static readonly Image Avatar = LoadAvatar();

    public static Icon TrayIcon => LoadIcon(SystemInformation.SmallIconSize);

    static Icon LoadIcon(Size? size)
    {
        using var s = Asm.GetManifestResourceStream("BackloggdLogger.Assets.app.ico")!;
        return size is { } sz ? new Icon(s, sz) : new Icon(s);
    }

    static Image LoadAvatar()
    {
        using var s = Asm.GetManifestResourceStream("BackloggdLogger.Assets.avatar.png")!;
        using var img = Image.FromStream(s);
        return new Bitmap(img);
    }
}

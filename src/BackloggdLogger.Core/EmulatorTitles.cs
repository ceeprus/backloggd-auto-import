using System.Text.RegularExpressions;

namespace BackloggdLogger.Core;

public enum EmulatorKind { Dolphin, Pcsx2, DuckStation, Rpcs3, Ryujinx, Cemu, Ppsspp, RetroArch, Generic }

// One record of a game seen in an emulator's window title.
public sealed record EmulatorTitle(string Title, string? Name, string? Id);

// An emulator runs many games and names the current one in its window title. Formats differ per emulator and
// version, so each parser returns null for anything that names no game (a version, a renderer, FPS, a bare ID).
public static partial class EmulatorTitles
{
    public static EmulatorKind? Detect(string exeFileName)
    {
        var n = exeFileName.Trim().ToLowerInvariant();
        if (!n.EndsWith(".exe", StringComparison.Ordinal)) return null;
        n = n[..^4];
        if (n == "dolphin") return EmulatorKind.Dolphin;
        if (n.StartsWith("pcsx2", StringComparison.Ordinal)) return EmulatorKind.Pcsx2;
        if (n.StartsWith("duckstation", StringComparison.Ordinal)) return EmulatorKind.DuckStation;
        if (n == "rpcs3") return EmulatorKind.Rpcs3;
        if (n == "ryujinx") return EmulatorKind.Ryujinx;
        if (n == "cemu") return EmulatorKind.Cemu;
        if (n.StartsWith("ppsspp", StringComparison.Ordinal)) return EmulatorKind.Ppsspp;
        if (n == "retroarch") return EmulatorKind.RetroArch;
        return null;
    }

    public static string Name(EmulatorKind kind) => kind switch
    {
        EmulatorKind.Pcsx2 => "PCSX2",
        EmulatorKind.Rpcs3 => "RPCS3",
        EmulatorKind.Ppsspp => "PPSSPP",
        _ => kind.ToString(),
    };

    public static EmulatorKind KindOf(GameRecord emulator) =>
        emulator.EmulatorKindOverride ?? Detect(Path.GetFileName(emulator.ExePath ?? "")) ?? EmulatorKind.Generic;

    // The record keeps a choice only when it differs from what the exe name says, so a later
    // change to Detect still applies to exes the user never toggled.
    public static void SetEmulator(GameRecord g, bool on)
    {
        var byName = Detect(Path.GetFileName(g.ExePath ?? "")) is not null;
        g.TreatAsEmulator = on == byName ? null : on;
    }

    public static EmulatorTitle Parse(GameRecord emulator, string title)
    {
        var exe = emulator.ExePath ?? "";
        var name = GameFromTitle(KindOf(emulator), title, Path.GetFileName(exe));
        return new EmulatorTitle(title, name, name is null ? null : GameIds.Emulator(exe, name));
    }

    public static string? GameFromTitle(EmulatorKind kind, string windowTitle, string? exeName = null)
    {
        var t = Whitespace().Replace(windowTitle ?? "", " ").Trim();
        if (t.Length == 0) return null;
        var names = OwnNames(kind, exeName);
        var raw = kind switch
        {
            EmulatorKind.Dolphin => Dolphin(t, names),
            EmulatorKind.Pcsx2 => TitleIsGame(t, "PCSX2", names),
            EmulatorKind.DuckStation => TitleIsGame(t, "DuckStation", names),
            EmulatorKind.Rpcs3 => Rpcs3(t, names),
            EmulatorKind.Ryujinx => Ryujinx(t),
            EmulatorKind.Cemu => Cemu(t),
            EmulatorKind.Ppsspp => After(t, " : "),
            EmulatorKind.RetroArch => RetroArch(t),
            _ => Generic(t, names),
        };
        if (raw is null) return null;
        var name = Finish(raw, names);
        if (name is null) return null;
        if (kind == EmulatorKind.Generic && name.Count(char.IsLetter) < 3) return null;
        return name;
    }

    // Per emulator

    static string? Dolphin(string t, string[] names)
    {
        var segs = Segments(t, " | ");
        if (segs.Length < 2) return null;
        var last = segs[^1];
        return IsJunk(last, names) ? null : DolphinId().Replace(last, "");
    }

    // PCSX2 and DuckStation title their game window with the game alone; their own windows start
    // with the emulator's name.
    static string? TitleIsGame(string t, string emulator, string[] names)
    {
        if (StartsWithName(t, emulator)) return null;
        t = Regex.Replace(t, $@"\s+-\s+{emulator}\b.*$", "", RegexOptions.IgnoreCase);
        if (t.Contains(" | ", StringComparison.Ordinal))
            return Segments(t, " | ").LastOrDefault(s => !IsJunk(s, names));
        return t;
    }

    static string? Rpcs3(string t, string[] names)
    {
        var segs = Segments(t, " | ");
        var last = segs.Length > 0 ? segs[^1] : "";
        var serial = Rpcs3Serial().IsMatch(last);
        if (!serial && (StartsWithName(t, "RPCS3") || segs.Length < 2)) return null;
        return IsJunk(last, names) ? null : Rpcs3Serial().Replace(last, "");
    }

    static string? Ryujinx(string t)
    {
        var m = RyujinxTitle().Match(t);
        if (!m.Success) return null;
        var s = m.Groups[1].Value;
        string prev;
        do
        {
            prev = s;
            s = RyujinxTail().Replace(s, "").Trim();
        } while (s != prev);
        return s;
    }

    static string? Cemu(string t)
    {
        var s = TrailingBracket().Replace(t, "");
        var i = s.LastIndexOf(']');
        return i < 0 ? null : s[(i + 1)..];
    }

    static string? RetroArch(string t)
    {
        var bar = t.IndexOf("||", StringComparison.Ordinal);
        if (bar >= 0) t = t[..bar].Trim();
        return After(t, " - ");
    }

    static string? Generic(string t, string[] names)
    {
        t = Brackets().Replace(t, " ");
        var parts = GenericSeparators().Split(t).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var keep = new List<string>();
        foreach (var p in parts)
        {
            var rest = StripName(p, names);
            if (rest is not null && !IsJunk(rest, names)) keep.Add(rest);
        }
        return keep.Count == 0 ? null : string.Join(" - ", keep);
    }

    // Shared

    static readonly string[] Emulators = { "Dolphin", "PCSX2", "DuckStation", "RPCS3", "Ryujinx", "Cemu", "PPSSPP", "RetroArch" };

    static string[] OwnNames(EmulatorKind kind, string? exeName)
    {
        var list = new List<string>(Emulators);
        if (!string.IsNullOrWhiteSpace(exeName))
        {
            var stem = Path.GetFileNameWithoutExtension(exeName.Trim());
            if (stem.Length >= 3) list.Insert(0, stem);
            var head = StemHead().Match(stem).Value;
            if (head.Length >= 3 && !head.Equals(stem, StringComparison.OrdinalIgnoreCase)) list.Insert(1, head);
        }
        return list.ToArray();
    }

    static string[] Segments(string t, string sep) =>
        t.Split(sep, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    static string? After(string t, string sep)
    {
        var i = t.IndexOf(sep, StringComparison.Ordinal);
        return i < 0 ? null : t[(i + sep.Length)..];
    }

    static bool StartsWithName(string t, string name) =>
        t.StartsWith(name, StringComparison.OrdinalIgnoreCase) && (t.Length == name.Length || !char.IsLetter(t[name.Length]));

    // The text left after a leading emulator or exe name, or null when the segment is only that
    // name (with a version or nothing after it).
    static string? StripName(string s, string[] names)
    {
        foreach (var n in names)
        {
            if (!StartsWithName(s, n)) continue;
            var rest = s[n.Length..].Trim(' ', '-', ':', '|', ',');
            return rest.Length == 0 || Version().IsMatch(rest) ? null : rest;
        }
        return s;
    }

    static bool IsJunk(string s, string[] names)
    {
        s = s.Trim(' ', '-', '|', ':', ',');
        if (s.Length == 0 || !s.Any(char.IsLetterOrDigit)) return true;
        if (Version().IsMatch(s) || Stats().IsMatch(s) || Renderer().IsMatch(s) || PureId().IsMatch(s)) return true;
        return StripName(s, names) is null;
    }

    static readonly string[] UiWindows =
    {
        "Settings", "Configuration", "Config", "Options", "Preferences", "Controls", "Controller", "Controllers",
        "Controller Settings", "Input", "Input Settings", "Graphics", "Graphics Settings", "Audio", "Audio Settings",
        "Log", "Logs", "Log Window", "Console", "Debugger", "Debug", "About", "Cheats", "Cheat Manager", "Game List",
        "Game Properties", "Properties", "Memory Card", "Memory Cards", "Memory Card Manager", "Memory Card Editor",
        "Achievements", "Netplay", "Patches", "Save Data Manager", "Title Update Manager", "DLC Manager", "Mod Manager",
        "Amiibo", "Shader Cache", "Update", "Updater", "Big Picture", "Loading", "Paused", "Untitled",
    };

    static bool IsUiWindow(string s) =>
        UiWindows.Any(w => s.Equals(w, StringComparison.OrdinalIgnoreCase)
            || (s.StartsWith(w, StringComparison.OrdinalIgnoreCase) && s.Length > w.Length && UiTail().IsMatch(s[w.Length..])));

    static string? Finish(string raw, string[] names)
    {
        var s = Whitespace().Replace(raw, " ").Trim();
        string prev;
        do
        {
            prev = s;
            s = TrailingGroup().Replace(s, "").Trim();
        } while (s != prev);
        s = GameNames.CleanTitle(s).Trim(' ', '-', '|', ':', ',', '~');
        if (!s.Any(char.IsLetter) || IsJunk(s, names) || IsUiWindow(s)) return null;
        return s;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s*\((?-i:[A-Z0-9]{6})\)\s*$")]
    private static partial Regex DolphinId();

    [GeneratedRegex(@"\s*\[(?-i:[A-Z]{4}\d{5})\]\s*$")]
    private static partial Regex Rpcs3Serial();

    [GeneratedRegex(@"(?i)^Ryujinx(?:\s+\S+)*?\s+-\s+(.+)$")]
    private static partial Regex RyujinxTitle();

    [GeneratedRegex(@"(?i)(?:\s*\(\s*(?:64|32)\s*-?\s*bit\s*\)|\s*\(\s*[0-9a-f]{16}\s*\)|(?:^|\s+)v\d+(?:\.\d+)*)\s*$")]
    private static partial Regex RyujinxTail();

    [GeneratedRegex(@"\s*\[[^\]]*\]\s*$")]
    private static partial Regex TrailingBracket();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s*\|+\s*|\s+[-–]\s+")]
    private static partial Regex GenericSeparators();

    [GeneratedRegex(@"^[A-Za-z]+")]
    private static partial Regex StemHead();

    [GeneratedRegex(@"^\s*(?:-|:|\[|\()")]
    private static partial Regex UiTail();

    // A version, a build or branch label, or a commit hash: "v1.2", "2412", "5.0-21088",
    // "0.0.33-17213-6b2e9e9b Alpha", "Nightly 2104".
    [GeneratedRegex(@"(?i)^(?:(?:v?\d+(?:[.\-_]\d+)*[a-z]?|[0-9a-f]{7,40}|nightly|canary|alpha|beta|dev|stable|release|preview|rc\d*|git|master|ldn[\d.]*)[\s\-]*)+$")]
    private static partial Regex Version();

    // FPS, speed, frame time or percent fragments.
    [GeneratedRegex(@"(?i)\b(?:fps|vps)\b|\d\s*%|\b\d+(?:\.\d+)?\s*ms\b|\b(?:speed|slot|frames?|cpu|gpu|ee|gs|vu|ui)\s*:\s*[\d.]|\blimiter\s*:")]
    private static partial Regex Stats();

    // Only renderer and CPU backend words: "Vulkan", "Direct3D 11", "JIT64 DC", "HLE".
    [GeneratedRegex(@"(?i)^(?:(?:jit(?:64|arm64)?|cached interpreter|interpreter|hle|lle|dc|sc|vulkan|opengl(?:\s*es)?|direct3d\s*\d*|d3d\s*\d*|dx\s*\d+|metal|software(?:\s+renderer)?|null)\s*)+$")]
    private static partial Regex Renderer();

    // A disc serial ("SLUS-21005"), a 16-hex Switch title id, a Wii U title id, or a 6-character
    // GameCube/Wii id with a digit in it ("GZLE01").
    [GeneratedRegex(@"^(?:[A-Z]{4}[-_ ]?\d{3}\.?\d{2}|[0-9A-Fa-f]{16}|[0-9A-Fa-f]{8}-[0-9A-Fa-f]{8}|(?=[A-Z]*\d)[A-Z0-9]{6})$")]
    private static partial Regex PureId();

    // A trailing serial, title id, region, disc or revision group: "[SLUS-21005]", "(GZLE01)",
    // "(USA)", "(En,Fr,De)", "(Disc 1)", "[Disc1of3]", "(Rev 1)", "[EU v208]".
    [GeneratedRegex(@"(?i)\s*[\(\[]\s*(?:(?-i:[A-Z]{4}[-_ ]?\d{3}\.?\d{2})|[0-9a-f]{16}|[0-9a-f]{8}-[0-9a-f]{8}|(?-i:(?=[A-Z0-9]*[A-Z])[A-Z0-9]{4}(?:[A-Z0-9]{2})?)|TitleId:[^\]\)]*|(?:usa|europe|japan|world|asia|korea|australia|brazil|canada|china|france|germany|italy|spain|uk|eu|us|jp|ntsc(?:-[ujk])?|pal|en|fr|de|es|it|nl|pt|sv|no|da|fi|ja|zh|ko|ru|pl)(?:\s*,\s*(?:usa|europe|japan|world|asia|korea|australia|brazil|canada|china|france|germany|italy|spain|uk|eu|us|jp|ntsc(?:-[ujk])?|pal|en|fr|de|es|it|nl|pt|sv|no|da|fi|ja|zh|ko|ru|pl))*(?:\s+v\d+(?:\.\d+)*)?|disc\s*\d+(?:\s*of\s*\d+)?|rev\s*[0-9a-z]+|v\d+(?:\.\d+)*)\s*[\)\]]\s*$")]
    private static partial Regex TrailingGroup();
}

// Raw window titles already written to emulators.log this run. Titles that differ only in their
// numbers (an FPS counter) count as one, and each emulator logs a bounded number of them.
public sealed partial class SeenTitles
{
    public const int MaxPerEmulator = 200;

    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    public bool FirstSight(string exePath, string title)
    {
        _counts.TryGetValue(exePath, out var n);
        if (n >= MaxPerEmulator) return false;
        if (!_seen.Add(exePath.ToLowerInvariant() + "\n" + Digits().Replace(title, "#"))) return false;
        _counts[exePath] = n + 1;
        return true;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}

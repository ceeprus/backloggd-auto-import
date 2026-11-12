using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BackloggdLogger.Core;
using Microsoft.Win32;

namespace BackloggdLogger;

interface ISteamSource
{
    uint RunningAppId();
    string? AppName(uint appId);

    // The folder the Steam app is installed in (steamapps\common\<dir>), when it can be read.
    string? InstallFolder(uint appId) => null;
}

interface IProcessSource
{
    IReadOnlySet<string> RunningExePaths(IEnumerable<string> trackedPaths);

    // The full paths of running processes whose exe name is a known emulator (EmulatorTitles.Detect).
    IReadOnlyList<string> RunningKnownEmulators();

    // The titles of the visible top-level windows of every running process of these exes, keyed
    // by the lowercased exe path. Exes that aren't running have no entry.
    IReadOnlyDictionary<string, IReadOnlyList<string>> WindowTitles(IEnumerable<string> exePaths);
}

sealed class RegistrySteamSource : ISteamSource
{
    readonly Dictionary<uint, string?> _names = new();

    public uint RunningAppId()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("RunningAppID") is int v ? unchecked((uint)v) : 0;
    }

    readonly Dictionary<uint, string?> _folders = new();

    public string? InstallFolder(uint appId)
    {
        if (_folders.TryGetValue(appId, out var cached)) return cached;
        string? folder = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath)
            {
                var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                var libraries = File.Exists(vdf) ? SteamFiles.LibraryPaths(File.ReadAllText(vdf)) : new[] { steamPath };
                foreach (var lib in libraries)
                {
                    var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
                    if (!File.Exists(acf)) continue;
                    if (SteamFiles.InstallDir(File.ReadAllText(acf)) is { } dir) folder = Path.GetFullPath(Path.Combine(lib, "steamapps", "common", dir));
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { folder = null; }
        if (folder is not null) _folders[appId] = folder;
        return folder;
    }

    public string? AppName(uint appId)
    {
        if (_names.TryGetValue(appId, out var cached)) return cached;
        string? name = null;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
        {
            if (key?.GetValue("SteamPath") is string steamPath)
            {
                var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                try
                {
                    var libraries = File.Exists(vdf) ? SteamFiles.LibraryPaths(File.ReadAllText(vdf)) : new[] { steamPath };
                    foreach (var lib in libraries)
                    {
                        var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
                        if (!File.Exists(acf)) continue;
                        name = SteamFiles.AppName(File.ReadAllText(acf));
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { name = null; }
            }
        }
        if (name is not null) _names[appId] = name;
        return name;
    }
}

sealed class WindowsProcessSource : IProcessSource
{
    public IReadOnlySet<string> RunningExePaths(IEnumerable<string> trackedPaths) =>
        Running(trackedPaths).Values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Names are compared first; the full path is read only for a match.
    public IReadOnlyList<string> RunningKnownEmulators()
    {
        var found = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (EmulatorTitles.Detect(p.ProcessName + ".exe") is null) continue;
                    var path = ProcessPaths.Get(p.Id);
                    if (path is not null && !found.Contains(path, StringComparer.OrdinalIgnoreCase)) found.Add(path);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return found;
    }

    // One window walk per poll, and only while one of the exes runs.
    public IReadOnlyDictionary<string, IReadOnlyList<string>> WindowTitles(IEnumerable<string> exePaths)
    {
        var byPid = Running(exePaths);
        var titles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (byPid.Count == 0) return titles;
        var lists = byPid.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, title) in TopWindows.Titles(byPid.Keys.ToHashSet()))
            lists[byPid[pid]].Add(title);
        foreach (var (path, list) in lists) titles[path] = list;
        return titles;
    }

    // Running processes of these exes: process id to lowercased exe path. A process that exits
    // or can't be opened while it is read is skipped.
    static Dictionary<int, string> Running(IEnumerable<string> paths)
    {
        var running = new Dictionary<int, string>();
        // One walk over the processes however many exes are looked for (every installed Epic and
        // GOG game is): names are compared first, and the full path is read only for a match.
        var byName = paths.ToLookup(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase);
        if (byName.Count == 0) return running;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (!byName.Contains(p.ProcessName)) continue;
                    var path = ProcessPaths.Get(p.Id);
                    if (path is not null && byName[p.ProcessName].Contains(path, StringComparer.OrdinalIgnoreCase))
                        running[p.Id] = path.ToLowerInvariant();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return running;
    }
}

static class TopWindows
{
    const int GW_OWNER = 4;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80;

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    // For a window of another process GetWindowText reads the stored caption and sends no
    // message, so a hung emulator can't stall the poll.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hwnd, char[] text, int max);

    // Visible, unowned, non-tool windows with a title. The game window of RPCS3 or Ryujinx is a
    // window of its own next to the main one; dialogs (settings, logs) are owned and left out.
    public static List<(int Pid, string Title)> Titles(IReadOnlySet<int> pids)
    {
        var found = new List<(int, string)>();
        var buffer = new char[512];
        EnumProc proc = (hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out var pid);
                if (!pids.Contains((int)pid)) return true;
                if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
                if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
                var n = GetWindowText(hwnd, buffer, buffer.Length);
                if (n > 0) found.Add(((int)pid, new string(buffer, 0, n)));
            }
            catch (Exception) { }
            return true;
        };
        EnumWindows(proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return found;
    }
}

static class ProcessPaths
{
    const uint QueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    public static string? Get(int pid)
    {
        var h = OpenProcess(QueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }
}

static class Fullscreen
{
    [DllImport("shell32.dll")]
    static extern int SHQueryUserNotificationState(out int state);

    // 2 = QUNS_BUSY, 3 = QUNS_RUNNING_D3D_FULL_SCREEN, 4 = QUNS_PRESENTATION_MODE, 7 = QUNS_APP
    public static bool IsBusy() => SHQueryUserNotificationState(out var s) == 0 && (s is 2 or 3 or 4 or 7);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr hWnd, int index);

    const int GWL_STYLE = -16;
    const int WS_CAPTION = 0x00C00000;

    // A window in front that covers its whole monitor without a title bar (a borderless game, a fullscreen video).
    // The desktop, the taskbar and a maximized window don't count.
    public static bool ForegroundCoversMonitor()
    {
        var w = GetForegroundWindow();
        if (w == IntPtr.Zero || !GetWindowRect(w, out var r)) return false;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(w, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        var mon = MonitorFromWindow(w, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref info)) return false;
        var m = info.rcMonitor;
        var covers = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        return covers && (GetWindowLong(w, GWL_STYLE) & WS_CAPTION) != WS_CAPTION;
    }
}

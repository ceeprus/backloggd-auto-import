namespace BackloggdLogger;

static class Paths
{
    static readonly string DefaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BackloggdLogger");

    internal static string? RootOverride { get; set; }

    public static string Root => RootOverride ?? DefaultRoot;
    public static string State => Path.Combine(Root, "state.json");
    public static string WebViewData => Path.Combine(Root, "webview");
    public static string Logs => Path.Combine(Root, "logs");
}

static class Log
{
    public static void Write(string area, string message)
    {
        try
        {
            Directory.CreateDirectory(Paths.Logs);
            new Core.LogFile(Path.Combine(Paths.Logs, area + ".log"), MaxBytes).Append($"{DateTime.Now:s} {message}{Environment.NewLine}");
        }
        catch (Exception) { }
    }

    const long MaxBytes = 1_000_000;
}

static class StorageCaps
{
    const long CacheCapBytes = 200L * 1024 * 1024;

    static Task? _clean;

    // True once the clean-up at startup is over, so nothing else uses the browser profile meanwhile.
    public static bool Finished => _clean?.IsCompleted ?? false;

    public static void Start() => _clean = Task.Run(() =>
    {
        try
        {
            var freed = Core.CacheCleaner.Run(Paths.WebViewData, CacheCapBytes);
            if (freed >= 0) Log.Write("storage", $"browser cache cleared, freed {freed / 1024 / 1024} MB");
        }
        catch (Exception) { }
    });
}

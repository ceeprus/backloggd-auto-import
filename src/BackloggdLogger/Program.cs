namespace BackloggdLogger;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "BackloggdLogger.SingleInstance", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("errors", "ui thread: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("errors", "unhandled: " + e.ExceptionObject);
        StorageCaps.Start();
        Application.Run(new TrayContext());
    }
}

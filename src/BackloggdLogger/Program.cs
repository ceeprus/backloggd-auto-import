namespace BackloggdLogger;

static class Program
{
    public const string UpdatedArg = "--updated";

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "BackloggdLogger.SingleInstance", out var first);
        // Right after an update the old copy may still be closing: wait for it instead of quitting.
        if (!first && !(args.Contains(UpdatedArg) && WaitFor(mutex))) return;
        SelfUpdate.CleanUp();
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("errors", "ui thread: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("errors", "unhandled: " + e.ExceptionObject);
        StorageCaps.Start();
        Application.Run(new TrayContext());
    }

    static bool WaitFor(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { return true; }
    }
}

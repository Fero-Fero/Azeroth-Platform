using Avalonia;

namespace AzerothPlatform.Launcher;

internal sealed class Program
{
    // Avalonia configuration; must not use any Avalonia, third-party APIs or SynchronizationContext
    // before AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Capture any startup/runtime crash to a log file so failures that would otherwise be an
        // instant, message-less exit (e.g. a missing Windows WebView2 runtime) are diagnosable.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write(e.ExceptionObject as Exception, "AppDomain.UnhandledException");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception, "UnobservedTaskException");
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Startup");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

/// <summary>Best-effort crash logger writing to the launcher's per-user data directory.</summary>
internal static class CrashLog
{
    public static void Write(Exception? ex, string source)
    {
        if (ex is null)
        {
            return;
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AzerothPlatformLauncher");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            var entry = $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(path, entry);
            Console.Error.WriteLine($"[CRASH -> {path}] {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // Logging a crash must never itself crash.
        }
    }
}

using Avalonia;
using AzerothPlatform.Launcher.Services;

namespace AzerothPlatform.Launcher;

internal sealed class Program
{
    // Avalonia configuration; must not use any Avalonia, third-party APIs or SynchronizationContext
    // before AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Elevated helper mode: an administrator copy of the launcher started only to open the shared
        // install folder to all users. It must not show a window or touch Avalonia.
        if (TryGetPrepareDirectory(args) is { } prepareDirectory)
        {
            Environment.Exit(PrepareSharedDirectory(prepareDirectory));
            return;
        }

        if (ShouldElevateOnStart() && InstallPathAccess.TryRelaunchElevated(args))
        {
            return;
        }

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

    /// <summary>
    /// Grants the all-users ACL on <paramref name="directory"/> and returns the process exit code. The
    /// distributor's pinned install path is read from settings so a custom location stays permitted while
    /// arbitrary paths passed on the command line are refused.
    /// </summary>
    private static int PrepareSharedDirectory(string directory)
    {
        string? distributorRoot = null;
        try
        {
            distributorRoot = new LauncherStateStore().LoadDefaults().DefaultInstallDirectory;
        }
        catch
        {
            // An unreadable settings file only narrows what the helper will accept.
        }

        return InstallPathAccess
            .PrepareSharedDirectoryAsync(directory, distributorRoot)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// True when a previous run could not open the install folder to all users and recorded that the
    /// launcher has to run elevated to write it at all. This is the fallback path: normally the folder is
    /// granted once and the launcher stays unelevated forever after.
    /// </summary>
    private static bool ShouldElevateOnStart()
    {
        if (!OperatingSystem.IsWindows() || GameLauncher.IsProcessElevated())
        {
            return false;
        }

        try
        {
            return new LauncherStateStore().Load().ElevateOnStart;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the directory from a <c>--prepare-install-dir &lt;path&gt;</c> invocation, or null for a
    /// normal start.
    /// </summary>
    private static string? TryGetPrepareDirectory(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], InstallPathAccess.PrepareArgument, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1];
            }
        }

        return null;
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

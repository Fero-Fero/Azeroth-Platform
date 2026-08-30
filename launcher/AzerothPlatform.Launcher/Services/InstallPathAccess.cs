using System.ComponentModel;
using System.Diagnostics;

namespace AzerothPlatform.Launcher.Services;

/// <summary>
/// Chooses where the client is installed and makes that folder usable by every account on the machine.
///
/// The launcher runs unelevated (see app.manifest). An elevated Wow.exe resolves <c>Data\</c> from
/// System32 and stops loading letter patches such as patch-W, so elevation is confined to a short-lived
/// child process (<see cref="PrepareArgument"/>) that grants the shared-folder ACL and exits.
/// </summary>
internal static class InstallPathAccess
{
    public const string DefaultFolderName = "Azeroth Platform";

    /// <summary>Command line switch that makes the launcher prepare a folder and exit immediately.</summary>
    public const string PrepareArgument = "--prepare-install-dir";

    /// <summary>
    /// Well-known SID of BUILTIN\Users. The group name is localised ("Benutzer", "Utilisateurs") and
    /// would not resolve on a non-English Windows.
    /// </summary>
    private const string AllUsersSid = "*S-1-5-32-545";

    private static readonly TimeSpan GrantTimeout = TimeSpan.FromMinutes(5);

    /// <summary>A machine-wide location under ProgramData, shared by every account.</summary>
    public static string MachineInstallDirectory(string folderName) =>
        Path.Combine(BaseDirectory(Environment.SpecialFolder.CommonApplicationData), FolderName(folderName));

    /// <summary>A per-user location, writable without any elevation but visible to one account only.</summary>
    public static string UserInstallDirectory(string folderName) =>
        Path.Combine(BaseDirectory(Environment.SpecialFolder.LocalApplicationData), FolderName(folderName));

    private static string FolderName(string folderName) =>
        string.IsNullOrWhiteSpace(folderName) ? DefaultFolderName : folderName.Trim();

    private static string BaseDirectory(Environment.SpecialFolder folder)
    {
        var baseDir = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return string.IsNullOrWhiteSpace(baseDir) ? AppContext.BaseDirectory : baseDir;
    }

    public static bool IsWritable(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string? probe = null;
        try
        {
            Directory.CreateDirectory(directory);
            probe = Path.Combine(directory, $".acl-write-{Guid.NewGuid():N}");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (probe is not null)
            {
                try { File.Delete(probe); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="directory"/> may be opened to all users.
    ///
    /// The elevated helper takes its target from the command line, so without this an administrator
    /// running the launcher could be induced to weaken the ACL on any path on the machine. Only a
    /// subdirectory of ProgramData or LocalAppData qualifies, plus the distributor's pinned install
    /// path when one is configured. The roots themselves are excluded: granting BUILTIN\Users modify
    /// rights across the whole of ProgramData would affect every application on the PC.
    /// </summary>
    public static bool IsAllowedSharedDirectory(string directory, string? distributorRoot = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string full;
        try
        {
            full = Normalize(directory);
        }
        catch
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(distributorRoot) && IsWithin(full, distributorRoot, allowExact: true))
        {
            return true;
        }

        return IsWithin(full, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), allowExact: false)
            || IsWithin(full, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), allowExact: false);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsWithin(string candidate, string? root, bool allowExact)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string rootFull;
        try
        {
            rootFull = Normalize(root);
        }
        catch
        {
            return false;
        }

        if (allowExact && string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs the privileged half of setup: create the folder and open it to all users. Invoked by the
    /// elevated child process; the return value is that process's exit code.
    /// </summary>
    public static async Task<int> PrepareSharedDirectoryAsync(
        string directory, string? distributorRoot, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedSharedDirectory(directory, distributorRoot))
        {
            return 2;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            return 1;
        }

        return await TryGrantAllUsersAccessAsync(directory, cancellationToken) ? 0 : 1;
    }

    /// <summary>
    /// Grants BUILTIN\Users modify rights on <paramref name="directory"/>, inheritable so content
    /// downloaded later is writable by other accounts, and applied to existing children so an install
    /// that predates the grant is covered too. Requires an elevated process.
    ///
    /// Shells out to icacls because <c>DirectorySecurity</c> is not available on this project's
    /// platform-neutral target framework.
    /// </summary>
    public static async Task<bool> TryGrantAllUsersAccessAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "icacls.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(directory);
            startInfo.ArgumentList.Add("/grant");
            startInfo.ArgumentList.Add($"{AllUsersSid}:(OI)(CI)M");
            startInfo.ArgumentList.Add("/T");
            // Keep going past individual files we cannot touch (locked, or owned by another account); a
            // partial grant still shares the folder for everything downloaded from here on.
            startInfo.ArgumentList.Add("/C");
            startInfo.ArgumentList.Add("/Q");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // Drained concurrently: icacls reporting per-file errors can fill the pipe buffer and block
            // the child before it exits.
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            if (!await WaitForExitAsync(process, cancellationToken))
            {
                return false;
            }

            await Task.WhenAll(stdout, stderr);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Relaunches this executable elevated to prepare <paramref name="directory"/>, and waits for it.
    /// Returns false when the UAC prompt is dismissed or the child reports failure, which is the
    /// caller's signal to fall back to a per-user folder or to elevating on every start.
    /// </summary>
    public static async Task<bool> TryPrepareSharedDirectoryElevatedAsync(
        string directory, string? distributorRoot, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !IsAllowedSharedDirectory(directory, distributorRoot))
        {
            return false;
        }

        if (GameLauncher.IsProcessElevated())
        {
            return await PrepareSharedDirectoryAsync(directory, distributorRoot, cancellationToken) == 0;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        try
        {
            // UseShellExecute is required for the runas verb, which is what raises the UAC prompt.
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
            };
            startInfo.ArgumentList.Add(PrepareArgument);
            startInfo.ArgumentList.Add(directory);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            return await WaitForExitAsync(process, cancellationToken) && process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // Includes ERROR_CANCELLED: the user dismissed the UAC prompt.
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for <paramref name="process"/>, giving up after <see cref="GrantTimeout"/>. Killing an
    /// elevated child from an unelevated parent fails, so a timed-out process is abandoned rather than
    /// treated as an error to recover from.
    /// </summary>
    private static async Task<bool> WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GrantTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return false;
        }
    }

    /// <summary>
    /// Relaunches this executable elevated with the original arguments and returns true when the child
    /// started, meaning the current (unelevated) process should exit.
    /// </summary>
    public static bool TryRelaunchElevated(IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsWindows() || GameLauncher.IsProcessElevated())
        {
            return false;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            return Process.Start(startInfo) is not null;
        }
        catch
        {
            return false;
        }
    }
}

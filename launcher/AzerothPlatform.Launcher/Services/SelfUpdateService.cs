using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AzerothPlatform.Launcher.Models;

namespace AzerothPlatform.Launcher.Services;

/// <summary>
/// Compares the baked launcher build version against the server's latest build and, when newer,
/// downloads the new exe and swaps it via a small helper batch that waits for this process to exit.
/// </summary>
public sealed class SelfUpdateService
{
    private readonly ILauncherArtifactSource _client;
    private readonly string _currentVersion;

    public SelfUpdateService(ILauncherArtifactSource client, string? currentVersion)
    {
        _client = client;
        _currentVersion = currentVersion ?? string.Empty;
    }

    /// <summary>Returns the newer version string when a launcher update is available, else null.</summary>
    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        var (version, _, available) = await _client.GetLatestAsync(cancellationToken);
        if (!available || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        if (string.IsNullOrEmpty(_currentVersion))
        {
            return null; // no baked version -> avoid nagging in dev
        }

        return IsNewer(version!, _currentVersion) ? version : null;
    }

    /// <summary>
    /// Decides whether <paramref name="available"/> is newer than <paramref name="current"/> using the
    /// four-part Release.Update.Minor.Patch scheme numerically (so 1.2.10.0 &gt; 1.2.9.0). Non-semantic
    /// version strings are treated as 0.0.0.0.
    /// </summary>
    private static bool IsNewer(string available, string current)
    {
        return CompareSemantic(NormalizeSemantic(available), NormalizeSemantic(current)) > 0;
    }

    private static string NormalizeSemantic(string version) =>
        version.Contains('.') ? version : "0.0.0.0";

    private static int CompareSemantic(string a, string b)
    {
        var sa = ParseSegments(a);
        var sb = ParseSegments(b);
        for (var i = 0; i < 4; i++)
        {
            var c = sa[i].CompareTo(sb[i]);
            if (c != 0) { return c; }
        }
        return 0;
    }

    private static int[] ParseSegments(string version)
    {
        var segments = new int[4];
        var parts = version.Split('.');
        for (var i = 0; i < 4 && i < parts.Length; i++)
        {
            int.TryParse(parts[i], out segments[i]);
        }
        return segments;
    }

    /// <summary>
    /// Downloads the new launcher next to the current exe and launches a helper that replaces the
    /// running exe once this process exits, then relaunches it. Windows only.
    /// </summary>
    public async Task ApplyUpdateAsync(CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Self-update is only supported on Windows.");
        }

        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the current executable path.");
        var dir = Path.GetDirectoryName(currentExe)!;
        var newExe = Path.Combine(dir, "launcher-update.exe");

        // Fetch the expected hash from the artifact source before downloading.
        var (_, expectedHash, _) = await _client.GetLatestAsync(cancellationToken);

        // Fail closed: the update MUST carry a server-published SHA-256. Without one there is nothing to
        // verify against, so an unverified executable could be silently substituted (MITM / rogue mirror).
        // Refuse the update rather than executing an unverified binary.
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            try { File.Delete(newExe); } catch { /* best effort */ }
            throw new InvalidOperationException(
                "The server did not publish an integrity hash for this launcher update, so it cannot be " +
                "verified. The update was aborted for your safety (rebuild the launcher on the server to " +
                "publish a hash).");
        }

        await _client.DownloadAsync(newExe, cancellationToken);

        // Verify the downloaded artifact against the server-published SHA-256 before replacing the
        // running exe. A tampered or corrupted download (e.g. a MITM on a plain-HTTP mirror) is rejected.
        var actualHash = await ComputeSha256Async(newExe, cancellationToken);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(newExe); } catch { /* best effort */ }
            throw new InvalidOperationException(
                "The downloaded launcher update failed its integrity check and was discarded. " +
                "The update may have been tampered with (possible man-in-the-middle).");
        }

        var pid = Environment.ProcessId;
        var script = Path.Combine(Path.GetTempPath(), $"acl-update-{pid}.cmd");
        var contents =
            "@echo off\r\n" +
            $":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul\r\n" +
            "if not errorlevel 1 (\r\n" +
            "  timeout /t 1 /nobreak >nul\r\n" +
            "  goto wait\r\n" +
            ")\r\n" +
            $"move /y \"{newExe}\" \"{currentExe}\" >nul\r\n" +
            $"start \"\" \"{currentExe}\"\r\n" +
            $"del \"%~f0\"\r\n";
        await File.WriteAllTextAsync(script, contents, cancellationToken);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        // Caller should shut down the app so the helper can replace the exe.
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }
}

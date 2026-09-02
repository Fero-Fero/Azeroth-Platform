using System.Diagnostics;
using AzerothPlatform.Infrastructure.Services.Modules;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Shallow fetch + hard reset. Skips the pack download when HEAD already matches the remote tip.
/// </summary>
internal static class GitRepoSync
{
    public static bool IsRepository(string directory) =>
        Directory.Exists(Path.Combine(directory, ".git"));

    /// <summary>
    /// <paramref name="Changed"/> is false when HEAD already matches the remote branch tip, so
    /// fetch is skipped. <paramref name="Error"/> is set when git failed.
    /// </summary>
    public static async Task<(bool Changed, string? Error)> EnsureLatestAsync(
        string directory,
        string repositoryUrl,
        string branch,
        CancellationToken cancellationToken)
    {
        var url = ModuleCatalogService.ValidateGitRepository(repositoryUrl);
        var safeBranch = ModuleCatalogService.ValidateGitRef(branch);
        Directory.CreateDirectory(directory);

        if (!IsRepository(directory))
        {
            var (initExit, _, initError) = await RunAsync(
                ["init"], directory, TimeSpan.FromSeconds(15), cancellationToken);
            if (initExit != 0)
            {
                return (false, FormatError("git init", initExit, initError));
            }
        }

        var localSha = await TryRevParseHeadAsync(directory, cancellationToken);
        var (lsExit, lsOutput, lsError) = await RunAsync(
            ["ls-remote", "--heads", "--", url, safeBranch],
            directory,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (lsExit != 0)
        {
            return (false, FormatError("git ls-remote", lsExit, lsError));
        }

        var remoteSha = ParseLsRemoteSha(lsOutput);
        if (remoteSha is null)
        {
            return (false, $"Remote branch '{safeBranch}' was not found.");
        }

        if (localSha is not null && string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null);
        }

        var (setUrlExit, _, _) = await RunAsync(
            ["remote", "set-url", "origin", url],
            directory,
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (setUrlExit != 0)
        {
            await RunAsync(
                ["remote", "add", "origin", url],
                directory,
                TimeSpan.FromSeconds(15),
                cancellationToken);
        }

        var (fetchExit, _, fetchError) = await RunAsync(
            ["fetch", "--depth", "1", "--", url, safeBranch],
            directory,
            TimeSpan.FromMinutes(3),
            cancellationToken);
        if (fetchExit != 0)
        {
            return (false, FormatError("git fetch", fetchExit, fetchError));
        }

        var (resetExit, _, resetError) = await RunAsync(
            ["reset", "--hard", "FETCH_HEAD"],
            directory,
            TimeSpan.FromMinutes(1),
            cancellationToken);
        if (resetExit != 0)
        {
            return (false, FormatError("git reset", resetExit, resetError));
        }

        return (true, null);
    }

    internal static string? ParseLsRemoteSha(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tab = line.IndexOfAny(['\t', ' ']);
            var sha = tab < 0 ? line : line[..tab];
            if (sha.Length >= 40)
            {
                return sha;
            }
        }

        return null;
    }

    private static async Task<string?> TryRevParseHeadAsync(string directory, CancellationToken cancellationToken)
    {
        var (exit, output, _) = await RunAsync(
            ["rev-parse", "HEAD"],
            directory,
            TimeSpan.FromSeconds(15),
            cancellationToken);
        var sha = output.Trim();
        return exit == 0 && sha.Length >= 40 ? sha : null;
    }

    private static string FormatError(string operation, int exitCode, string error) =>
        string.IsNullOrWhiteSpace(error)
            ? $"{operation} exited with code {exitCode}"
            : error.Trim();

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        GitExecutable.ApplyTo(process.StartInfo);
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new InvalidOperationException($"Timed out running git {string.Join(' ', arguments)}.");
        }

        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process may have exited between the check and the kill.
        }
    }
}

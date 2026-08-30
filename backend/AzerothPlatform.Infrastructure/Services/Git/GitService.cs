using AzerothPlatform.Core.Services.Interfaces;
using System.Diagnostics;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Git adapter backed by the local git executable.
/// </summary>
public sealed class GitService : IGitService
{
    public async Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            GitExecutable.ApplyTo(process.StartInfo);
            process.StartInfo.ArgumentList.Add("--version");

            process.Start();
            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListRemoteBranchesAsync(
        string repositoryUrl, CancellationToken cancellationToken = default)
    {
        // The URL is validated by the caller. Pass it via ArgumentList (not a joined command line) and
        // with a "--" separator so it can never be interpreted as a git option.
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        GitExecutable.ApplyTo(process.StartInfo);
        process.StartInfo.ArgumentList.Add("ls-remote");
        process.StartInfo.ArgumentList.Add("--heads");
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add(repositoryUrl);
        // Never block on interactive credential prompts for a private/invalid repo.
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        // Bound the wait so a slow/unreachable remote cannot hang the request indefinitely.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new InvalidOperationException("Timed out while listing branches for the repository.");
        }

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? "the repository could not be reached." : error.Trim();
            throw new InvalidOperationException($"Could not list branches: {detail}");
        }

        var branches = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Each line is "<sha>\trefs/heads/<branch>".
            var tab = line.IndexOf('\t');
            if (tab < 0)
            {
                continue;
            }

            var reference = line[(tab + 1)..].Trim();
            const string prefix = "refs/heads/";
            if (reference.StartsWith(prefix, StringComparison.Ordinal))
            {
                branches.Add(reference[prefix.Length..]);
            }
        }

        return branches
            .Distinct(StringComparer.Ordinal)
            .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            // Best effort - the process may have exited between the check and the kill.
        }
    }
}

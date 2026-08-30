using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using IOPath = System.IO.Path;

namespace AzerothPlatform.Infrastructure.Services.Shared;

/// <summary>
/// Collects scratch that outlived the process that made it. <see cref="TempWorkspace"/> deletes on
/// dispose, but a killed container or a torn-down host never runs the <c>finally</c>, and the manager's
/// scratch runs to gigabytes (client archives, MPQ extracts, armory image staging), so orphans cannot
/// just be left to the operating system.
/// </summary>
public sealed class TempWorkspaceSweeper : BackgroundService
{
    /// <summary>
    /// How stale an entry must be before it is collected. Anything younger may belong to work in
    /// flight - a client upload can spend a long time between its last write and its final rename,
    /// and a second manager instance sharing this temp directory would have its scratch deleted
    /// underneath it.
    /// </summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// Prefixes written straight into the OS temp directory before <see cref="TempWorkspace.Root"/>
    /// existed. Swept so an upgraded install cleans up after its predecessor; new scratch lands under
    /// the root instead and is covered by the ordinary pass.
    /// </summary>
    private static readonly string[] LegacyPrefixes = ["azp-", "armory-", "addon-stage-"];

    private readonly ILogger<TempWorkspaceSweeper> _logger;
    private readonly TimeProvider _time;

    public TempWorkspaceSweeper(ILogger<TempWorkspaceSweeper> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = Sweep(_time.GetUtcNow());
                if (removed > 0)
                {
                    _logger.LogInformation("Removed {Count} stale temporary item(s) from {Root}.", removed, TempWorkspace.Root);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Temporary workspace sweep failed; retrying at the next interval.");
            }

            try
            {
                await Task.Delay(Interval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Deletes every entry older than <see cref="MaxAge"/> and returns how many went.</summary>
    internal static int Sweep(DateTimeOffset now) =>
        Sweep(now, TempWorkspace.Root, IOPath.GetTempPath());

    internal static int Sweep(DateTimeOffset now, string root, string legacyRoot)
    {
        var cutoff = now - MaxAge;
        var removed = 0;

        foreach (var entry in StaleEntries(cutoff, root, legacyRoot))
        {
            if (TempWorkspace.TryDelete(entry.Path, entry.IsDirectory))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Newest sign of life on an entry. A directory's own timestamp only moves when its immediate
    /// children change, so a job writing deep inside one would otherwise look untouched; the direct
    /// children are folded in to catch that one level down.
    /// </summary>
    private static DateTimeOffset LastActivity(string path, bool isDirectory)
    {
        if (!isDirectory)
        {
            var file = new FileInfo(path);
            return Newest(file.CreationTimeUtc, file.LastWriteTimeUtc);
        }

        var directory = new DirectoryInfo(path);
        var touched = Newest(directory.CreationTimeUtc, directory.LastWriteTimeUtc);
        foreach (var child in directory.EnumerateFileSystemInfos())
        {
            touched = Newest(touched.UtcDateTime, child.LastWriteTimeUtc);
        }

        return touched;

        static DateTimeOffset Newest(DateTime left, DateTime right) =>
            new(left > right ? left : right, TimeSpan.Zero);
    }

    private static IEnumerable<(string Path, bool IsDirectory)> StaleEntries(
        DateTimeOffset cutoff, string root, string legacyRoot)
    {
        foreach (var entry in Enumerate(root, _ => true))
        {
            yield return entry;
        }

        // The manager's own root sits in the OS temp directory, so exclude it from the legacy pass.
        foreach (var entry in Enumerate(
                     legacyRoot,
                     name => LegacyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
        {
            yield return entry;
        }

        IEnumerable<(string Path, bool IsDirectory)> Enumerate(string directory, Func<string, bool> matches)
        {
            if (!Directory.Exists(directory))
            {
                yield break;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (var path in entries)
            {
                if (IOPath.TrimEndingDirectorySeparator(path)
                        .Equals(IOPath.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
                    || !matches(IOPath.GetFileName(path)))
                {
                    continue;
                }

                var isDirectory = Directory.Exists(path);
                DateTimeOffset touched;
                try
                {
                    touched = LastActivity(path, isDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (touched < cutoff)
                {
                    yield return (path, isDirectory);
                }
            }
        }
    }
}

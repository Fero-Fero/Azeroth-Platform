using AzerothPlatform.ClientContent;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Patches;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Manages each stack's base WoW client in its <c>client-base</c> Docker volume (volume-first; no persistent
/// manager mirror). Admins upload on a stack's Client tab; content is extracted into the stack volume
/// directly.
/// </summary>
public sealed class ClientService : IClientService
{
    private readonly ClientDistributionOptions _options;
    private readonly ClientDownloadOptions _downloadOptions;
    private readonly DockerOptions _dockerOptions;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BaseClientDownloader _downloader;
    private readonly IClientJobService _clientJobs;
    private readonly IClientContainerService _clientContainer;
    private readonly ILogger<ClientService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClientService(
        IOptions<ClientDistributionOptions> options,
        IOptions<ClientDownloadOptions> downloadOptions,
        IOptions<DockerOptions> dockerOptions,
        IRemoteEngineService remoteEngine,
        IServiceScopeFactory scopeFactory,
        BaseClientDownloader downloader,
        IClientJobService clientJobs,
        IClientContainerService clientContainer,
        ILogger<ClientService> logger)
    {
        _options = options.Value;
        _downloadOptions = downloadOptions.Value;
        _dockerOptions = dockerOptions.Value;
        _remoteEngine = remoteEngine;
        _scopeFactory = scopeFactory;
        _downloader = downloader;
        _clientJobs = clientJobs;
        _clientContainer = clientContainer;
        _logger = logger;
    }

    /// <summary>Name the staged archive carries inside its work volume.</summary>
    private const string UploadArchiveEntryName = "upload.archive";

    private static string ClientBaseVolume(string stackId) =>
        DockerComposeOverrideGenerator.ClientBaseVolumeName(stackId);

    private async Task<ManagedStackEntity?> GetStackAsync(string stackId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzerothCoreDbContext>();
        return await db.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
    }

    public async Task<ClientBaseInfoDto> GetBaseInfoAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken)
            ?? throw new InvalidOperationException("Stack was not found.");
        var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, ClientBaseVolume(stackId), cancellationToken);
        var info = BuildInfo(stackId, summary);
        info.Manifest = await _clientContainer.GetManifestStatusAsync(stackId, cancellationToken: cancellationToken);
        return info;
    }

    public async Task<ClientBaseInfoDto> DownloadBaseClientAsync(
        string stackId,
        string? url = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(resolved))
        {
            resolved = (_downloadOptions.BaseClientUrl ?? string.Empty).Trim();
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException("A base-client download URL is required.");
        }

        if (!IsAllowedDownloadUrl(resolved))
        {
            throw new InvalidOperationException("The download URL must be an http or https link.");
        }

        _clientJobs.ReportProgress(stackId, "Downloading the base client…");
        if (BaseClientDownloader.IsGoogleDriveFolder(resolved))
        {
            return await DownloadDriveFolderAsync(stackId, resolved, cancellationToken);
        }

        await using var stream = await _downloader.DownloadAsync(resolved, cancellationToken);
        _clientJobs.ReportProgress(stackId, "Extracting and locating the WoW client…");
        return await UploadBaseClientAsync(stackId, stream, cancellationToken);
    }

    private static bool IsAllowedDownloadUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async Task<ClientBaseInfoDto> DownloadDriveFolderAsync(
        string stackId,
        string folderUrl,
        CancellationToken cancellationToken)
    {
        var stack = await GetStackAsync(stackId, cancellationToken)
            ?? throw new InvalidOperationException("Stack was not found.");
        var volumeName = ClientBaseVolume(stackId);
        var progress = new Progress<string>(message => _clientJobs.ReportProgress(stackId, message));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _downloader.DownloadGoogleDriveFolderAsync(
                folderUrl,
                async (relativePath, stream, token) =>
                {
                    if (ClientBaseMergePolicy.ShouldPreservePlatformContent(relativePath))
                    {
                        return;
                    }

                    await _remoteEngine.WriteVolumeFileFromStreamAsync(stack, volumeName, relativePath, stream, token);
                },
                progress,
                cancellationToken);

            await PromoteClientRootInVolumeAsync(stack, volumeName, cancellationToken);
            await PurgePreservedPlatformContentFromVolumeAsync(stack, volumeName, cancellationToken);

            var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, volumeName, cancellationToken);
            if (!summary.HasWowExe && !summary.HasDataMpq)
            {
                throw new InvalidOperationException(
                    "The Google Drive folder does not look like a WoW client (no Wow.exe or Data/*.MPQ found).");
            }

            TryDeleteLegacyGameMirror(stackId);
            _logger.LogInformation("Base client for stack {StackId} installed from Google Drive folder.", stackId);
            return await FinalizeContentChangeAsync(stackId, summary, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ClientBaseInfoDto> RescanBaseAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken)
            ?? throw new InvalidOperationException("Stack was not found.");
        return await FinalizeContentChangeAsync(stackId, stack, ClientBaseVolume(stackId), cancellationToken);
    }

    public async Task<ClientBaseInfoDto> PurgeClientContentAsync(
        string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken)
            ?? throw new InvalidOperationException("Stack was not found.");

        var baseVolume = ClientBaseVolume(stackId);
        var overlayVolume = DockerComposeOverrideGenerator.ClientOverlayVolumeName(stackId);
        var cacheVolume = DockerComposeOverrideGenerator.ClientCacheVolumeName(stackId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _clientJobs.ReportProgress(stackId, "Clearing the base client…");
            await _remoteEngine.ClearVolumeContentsAsync(stack, baseVolume, cancellationToken);

            _clientJobs.ReportProgress(stackId, "Clearing published patches and addons…");
            await _remoteEngine.ClearVolumeContentsAsync(stack, overlayVolume, cancellationToken);

            _clientJobs.ReportProgress(stackId, "Resetting the launcher manifest…");
            await _remoteEngine.DeleteVolumePathsAsync(
                stack, cacheVolume, ClientManifestBuilder.CacheBookkeepingFiles, cancellationToken);

            // The overlay volume is seeded from this mirror on the next patch apply, so leaving it would
            // resurrect the MPQs that were just purged.
            _clientJobs.ReportProgress(stackId, "Clearing the manager's overlay copy…");
            ClearOverlayMirror(stackId);

            _logger.LogWarning(
                "Purged all client content for stack {StackId}: volumes {BaseVolume}, {OverlayVolume} and the "
                + "manifest bookkeeping in {CacheVolume}.",
                stackId, baseVolume, overlayVolume, cacheVolume);
        }
        finally
        {
            _gate.Release();
        }

        return await FinalizeContentChangeAsync(stackId, stack, baseVolume, cancellationToken);
    }

    /// <summary>Empties the manager-side overlay mirror, keeping the directory so patch apply can refill it.</summary>
    private void ClearOverlayMirror(string stackId)
    {
        var overlayDir = MigrationLayout.ClientOverlayDir(StackRoot(stackId));
        if (!Directory.Exists(overlayDir))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(overlayDir))
        {
            TryDelete(entry, isDirectory: Directory.Exists(entry));
        }
    }

    private string StackRoot(string stackId)
    {
        var buildsPath = Path.IsPathRooted(_dockerOptions.BuildsPath)
            ? _dockerOptions.BuildsPath
            : Path.GetFullPath(_dockerOptions.BuildsPath);
        return Path.Combine(buildsPath, stackId);
    }

    public async Task<ClientBrowseResultDto> BrowseAsync(string stackId, string relativePath, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var normalized = NormalizeRelative(relativePath);
        var result = new ClientBrowseResultDto { Path = normalized };

        var baseVolume = ClientBaseVolume(stackId);
        var overlayVolume = DockerComposeOverrideGenerator.ClientOverlayVolumeName(stackId);
        var baseSummary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, baseVolume, cancellationToken);
        var overlaySummary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, overlayVolume, cancellationToken);
        if (!baseSummary.VolumeExists && !overlaySummary.VolumeExists)
        {
            return result;
        }

        var baseEntries = baseSummary.VolumeExists
            ? await _remoteEngine.ListVolumeDirectoryAsync(stack, baseVolume, normalized, cancellationToken)
            : [];
        var overlayEntries = overlaySummary.VolumeExists
            ? await _remoteEngine.ListVolumeDirectoryAsync(stack, overlayVolume, normalized, cancellationToken)
            : [];

        result.Entries.AddRange(ClientBrowseMerger.Merge(baseEntries, overlayEntries));
        result.Exists = normalized.Length == 0
            ? baseSummary.FileCount > 0 || overlaySummary.FileCount > 0 || result.Entries.Count > 0
            : result.Entries.Count > 0;
        return result;
    }

    public async Task<ClientBaseInfoDto> DeleteEntryAsync(string stackId, string relativePath, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRelative(relativePath);
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("Refusing to delete the base client root. Delete individual files or folders instead.");
        }

        if (ClientBaseMergePolicy.IsProtectedStockMpq(normalized))
        {
            throw new InvalidOperationException(
                "Default client archives (common, common-2, expansion, lichking, patch, patch-2, patch-3) cannot be deleted.");
        }

        var stack = await GetStackAsync(stackId, cancellationToken)
            ?? throw new InvalidOperationException("Stack was not found.");
        var baseVolume = ClientBaseVolume(stackId);
        var overlayVolume = DockerComposeOverrideGenerator.ClientOverlayVolumeName(stackId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _remoteEngine.DeleteVolumePathsAsync(stack, overlayVolume, [normalized], cancellationToken);
            await _remoteEngine.DeleteVolumePathsAsync(stack, baseVolume, [normalized], cancellationToken);
            _logger.LogInformation(
                "Deleted '{Path}' from client base and overlay volumes for stack {StackId}.",
                normalized,
                stackId);
        }
        finally
        {
            _gate.Release();
        }

        return await FinalizeContentChangeAsync(stackId, stack, baseVolume, cancellationToken);
    }

    /// <summary>
    /// The single exit point for every content mutation: reads back the volume and refreshes the
    /// launcher manifest so the change reaches players. Any refresh failure is folded into the returned
    /// info as <see cref="ClientBaseInfoDto.ManifestWarning"/> rather than swallowed, because a silent
    /// failure here is exactly what makes the client hash look "stuck" after an edit.
    /// </summary>
    private async Task<ClientBaseInfoDto> FinalizeContentChangeAsync(
        string stackId,
        VolumeTreeSummary summary,
        CancellationToken cancellationToken)
    {
        var info = BuildInfo(stackId, summary);
        info.ManifestWarning = await RefreshLauncherManifestAsync(stackId, cancellationToken);
        info.Manifest = await _clientContainer.GetManifestStatusAsync(stackId, refresh: true, cancellationToken);
        return info;
    }

    /// <inheritdoc cref="FinalizeContentChangeAsync(string, VolumeTreeSummary, CancellationToken)"/>
    private async Task<ClientBaseInfoDto> FinalizeContentChangeAsync(
        string stackId,
        ManagedStackEntity stack,
        string volumeName,
        CancellationToken cancellationToken)
    {
        var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, volumeName, cancellationToken);
        return await FinalizeContentChangeAsync(stackId, summary, cancellationToken);
    }

    /// <summary>
    /// Rebuilds the client-server manifest so launchers pick the change up on their next check. Returns
    /// null on success, or an operator-facing warning describing why propagation is delayed.
    /// </summary>
    private async Task<string?> RefreshLauncherManifestAsync(string stackId, CancellationToken cancellationToken)
    {
        try
        {
            await _clientContainer.RescanAsync(stackId, cancellationToken);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Client content changed for stack {StackId} but the launcher manifest refresh failed.", stackId);
            return
                "The files were changed, but the launcher manifest could not be refreshed right now "
                + $"({ex.Message.Trim()}). The client file server picks the change up on its own within a few "
                + "seconds of being reachable; start it if it is stopped.";
        }
    }

    public async Task<ClientBaseInfoDto> UploadFileAsync(
        string stackId, string relativeDir, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken)
            ?? throw new InvalidOperationException("Stack was not found.");
        var safeName = SanitizeFileName(fileName);
        var normalizedDir = NormalizeRelative(relativeDir);
        var relativeFile = CombineRelative(normalizedDir, safeName);
        ValidateVolumeRelative(relativeFile);

        // The browser shows base and overlay merged, so a dropped file has to land in whichever layer
        // owns that path. Writing a letter patch or an addon into the base would create a copy the
        // merge policy then ignores: an edit that looks like it worked and changes nothing.
        var targetVolume = ClientBaseMergePolicy.ShouldPreservePlatformContent(relativeFile)
            ? DockerComposeOverrideGenerator.ClientOverlayVolumeName(stackId)
            : ClientBaseVolume(stackId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _remoteEngine.WriteVolumeFileFromStreamAsync(
                stack, targetVolume, relativeFile, content, cancellationToken);
            _logger.LogInformation(
                "Uploaded '{Path}' into volume {Volume} for stack {StackId}.", relativeFile, targetVolume, stackId);
        }
        finally
        {
            _gate.Release();
        }

        return await FinalizeContentChangeAsync(stackId, stack, ClientBaseVolume(stackId), cancellationToken);
    }

    public async Task<ClientBaseInfoDto> UploadBaseClientAsync(string stackId, Stream archiveStream, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken)
            ?? throw new InvalidOperationException("Stack was not found.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var volumeName = ClientBaseVolume(stackId);

            if (stack.DeploymentTarget == DeploymentTarget.External)
            {
                return await InstallBaseClientFromStreamAsync(
                    stackId, stack, volumeName, archiveStream, cancellationToken);
            }

            return await UploadBaseClientLocallyAsync(stackId, stack, volumeName, archiveStream, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> StageBaseClientArchiveAsync(
        string stackId,
        Stream archiveStream,
        CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken)
            ?? throw new InvalidOperationException("Stack was not found.");

        if (_clientJobs.GetStatus(stackId)?.IsRunning == true)
        {
            throw new InvalidOperationException("A client operation is already running for this stack.");
        }

        // Reclaim anything a previous upload left behind before we consume space for this one.
        CleanupUploadStaging(stackId, stagingDir: null);
        await SweepOrphanedUploadVolumesAsync(stack, cancellationToken);

        // Sniff the format before choosing where the bytes land: everything the engine-side extractor
        // understands streams into a throwaway volume, so the manager never holds a copy of the client.
        var (prepared, isRar) = await PrepareArchiveStreamAsync(archiveStream, cancellationToken);
        return isRar
            ? (await StageArchiveOnManagerDiskAsync(stackId, prepared, cancellationToken)).ToString()
            : (await StageArchiveInWorkVolumeAsync(stackId, stack, prepared, cancellationToken)).ToString();
    }

    /// <summary>
    /// Streams the archive into a throwaway Docker volume on the stack's engine as
    /// <c>upload.archive</c>. Docker creates the named volume on first mount, so this needs no separate
    /// provisioning step.
    /// </summary>
    private async Task<StagedClientArchive> StageArchiveInWorkVolumeAsync(
        string stackId,
        ManagedStackEntity stack,
        Stream archiveStream,
        CancellationToken cancellationToken)
    {
        var workVolume = UploadWorkVolumeName();
        try
        {
            await _remoteEngine.WriteVolumeFileFromStreamAsync(
                stack, workVolume, UploadArchiveEntryName, archiveStream, cancellationToken);
        }
        catch
        {
            await TryRemoveWorkVolumeAsync(stack, workVolume);
            throw;
        }

        _logger.LogInformation(
            "Staged base client archive for stack {StackId} in work volume {Volume}.", stackId, workVolume);
        return StagedClientArchive.InWorkVolume(workVolume);
    }

    /// <summary>
    /// Fallback for RAR, which the engine-side extractor cannot open: the archive is written to manager
    /// disk so SharpCompress can walk it entry by entry.
    /// </summary>
    private async Task<StagedClientArchive> StageArchiveOnManagerDiskAsync(
        string stackId,
        Stream archiveStream,
        CancellationToken cancellationToken)
    {
        var stagingDir = CreateUploadStagingDir(stackId);
        var tempArchive = Path.Combine(stagingDir, UploadArchiveEntryName);
        try
        {
            await using (var file = new FileStream(
                tempArchive, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await archiveStream.CopyToAsync(file, cancellationToken);
            }

            var size = new FileInfo(tempArchive).Length;
            if (size == 0)
            {
                throw new InvalidOperationException("The uploaded file was empty.");
            }

            _logger.LogInformation(
                "Staged RAR base client archive for stack {StackId} at {Path} ({Size} bytes).",
                stackId, tempArchive, size);
            return StagedClientArchive.OnManagerDisk(tempArchive);
        }
        catch
        {
            CleanupUploadStaging(stackId, stagingDir);
            throw;
        }
    }

    public async Task DiscardStagedBaseClientArchiveAsync(string stackId, string stagingToken)
    {
        var staged = StagedClientArchive.Parse(stagingToken);
        if (staged.Kind == StagedClientArchiveKind.WorkVolume)
        {
            var stack = await GetStackAsync(stackId, CancellationToken.None);
            if (stack is not null)
            {
                await TryRemoveWorkVolumeAsync(stack, staged.Location);
            }

            return;
        }

        CleanupUploadStaging(stackId, Path.GetDirectoryName(staged.Location));
    }

    private const string UploadWorkVolumePrefix = "acore-client-upload-";

    private static string UploadWorkVolumeName() => $"{UploadWorkVolumePrefix}{Guid.NewGuid():N}";

    private async Task SweepOrphanedUploadVolumesAsync(ManagedStackEntity stack, CancellationToken cancellationToken)
    {
        try
        {
            await _remoteEngine.RemoveUnusedVolumesByPrefixAsync(
                stack, UploadWorkVolumePrefix, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not sweep orphaned client upload volumes; continuing with the upload.");
        }
    }

    private async Task TryRemoveWorkVolumeAsync(ManagedStackEntity stack, string volumeName)
    {
        try
        {
            await _remoteEngine.RemoveVolumeAsync(stack, volumeName, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not remove client upload work volume {Volume}; it will be swept later.", volumeName);
        }
    }

    public async Task<ClientBaseInfoDto> InstallStagedBaseClientAsync(
        string stackId,
        string stagingToken,
        CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken)
            ?? throw new InvalidOperationException("Stack was not found.");
        var staged = StagedClientArchive.Parse(stagingToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return staged.Kind == StagedClientArchiveKind.WorkVolume
                ? await InstallStagedWorkVolumeAsync(stackId, stack, staged.Location, cancellationToken)
                : await InstallStagedDiskArchiveAsync(stackId, stack, staged.Location, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Installs an archive already sitting in a work volume: the engine extracts it straight into the
    /// base volume, so the bytes never travel back through the manager.
    /// </summary>
    private async Task<ClientBaseInfoDto> InstallStagedWorkVolumeAsync(
        string stackId,
        ManagedStackEntity stack,
        string workVolume,
        CancellationToken cancellationToken)
    {
        var volumeName = ClientBaseVolume(stackId);
        try
        {
            _clientJobs.ReportProgress(stackId, "Extracting the client archive on the engine…");
            var result = await _remoteEngine.ExtractArchiveVolumeIntoVolumeAsync(
                stack, workVolume, UploadArchiveEntryName, volumeName, cancellationToken);

            await EnsureLooksLikeClientAsync(stack, volumeName, cancellationToken);
            TryDeleteLegacyGameMirror(stackId);

            _logger.LogInformation(
                "Base client for stack {StackId} installed into volume {Volume}: {Installed} files, {Purged} leftovers removed.",
                stackId, volumeName, result.FilesInstalled, result.FilesPurged);
            return await FinalizeContentChangeAsync(stackId, stack, volumeName, cancellationToken);
        }
        finally
        {
            await TryRemoveWorkVolumeAsync(stack, workVolume);
        }
    }

    /// <summary>Installs a RAR archive staged on manager disk (the engine-side extractor cannot open it).</summary>
    private async Task<ClientBaseInfoDto> InstallStagedDiskArchiveAsync(
        string stackId,
        ManagedStackEntity stack,
        string archivePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            throw new InvalidOperationException("The uploaded client archive is no longer on disk.");
        }

        var stagingDir = Path.GetDirectoryName(archivePath);
        try
        {
            var volumeName = ClientBaseVolume(stackId);
            if (stack.DeploymentTarget == DeploymentTarget.External)
            {
                await using var stream = new FileStream(
                    archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
                return await InstallBaseClientFromStreamAsync(
                    stackId, stack, volumeName, stream, cancellationToken);
            }

            var extractDir = Path.Combine(stagingDir!, "extract");
            Directory.CreateDirectory(extractDir);
            return await ExtractArchiveAndSeedVolumeAsync(
                stackId, stack, volumeName, archivePath, extractDir, cancellationToken);
        }
        finally
        {
            CleanupUploadStaging(stackId, stagingDir);
        }
    }

    /// <summary>
    /// Makes the volume match what the upload actually installed by deleting everything else, so files
    /// removed from a re-uploaded client do not survive in the volume forever. Used by the archive
    /// formats the engine-side installer cannot handle (RAR); the engine script does its own equivalent
    /// pass for everything else.
    ///
    /// The upload is treated as the operator's statement of what the base client is, so leftovers are
    /// removed however many there are. Whether the archive is a client at all is decided beforehand, on
    /// structure rather than size.
    /// </summary>
    private async Task PurgeVolumeLeftoversAsync(
        string stackId,
        ManagedStackEntity stack,
        string volumeName,
        IReadOnlySet<string> installedPaths,
        CancellationToken cancellationToken)
    {
        var existing = await _remoteEngine.ListVolumeFilesAsync(stack, volumeName, cancellationToken);
        var leftovers = existing
            .Select(entry => NormalizeRelative(entry.RelativePath))
            .Where(path => path.Length > 0 && !installedPaths.Contains(path))
            .ToList();

        if (leftovers.Count == 0)
        {
            return;
        }

        await _remoteEngine.DeleteVolumePathsAsync(stack, volumeName, leftovers, cancellationToken);
        _logger.LogInformation(
            "Removed {Count} leftover file(s) from the base volume for stack {StackId}.", leftovers.Count, stackId);
    }

    /// <summary>Relative, forward-slash paths of every file under a directory.</summary>
    private static HashSet<string> EnumerateRelativeFiles(string root)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            paths.Add(NormalizeRelative(Path.GetRelativePath(root, file)));
        }

        return paths;
    }

    private async Task EnsureLooksLikeClientAsync(
        ManagedStackEntity stack, string volumeName, CancellationToken cancellationToken)
    {
        var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, volumeName, cancellationToken);
        if (!summary.HasWowExe && !summary.HasDataMpq)
        {
            throw new InvalidOperationException(
                "The uploaded archive does not look like a WoW client (no Wow.exe or Data/*.MPQ found).");
        }
    }

    private async Task<ClientBaseInfoDto> InstallBaseClientFromStreamAsync(
        string stackId,
        ManagedStackEntity stack,
        string volumeName,
        Stream archiveStream,
        CancellationToken cancellationToken)
    {
        _clientJobs.ReportProgress(stackId, "Streaming the archive into the remote volume…");
        var (preparedStream, isRar) = await PrepareArchiveStreamAsync(archiveStream, cancellationToken);
        if (isRar)
        {
            _logger.LogInformation(
                "Streaming RAR client archive entry-by-entry to remote volume {Volume} for stack {StackId}.",
                volumeName,
                stackId);
            await UploadBaseClientByEntryStreamingAsync(stack, volumeName, preparedStream, cancellationToken);
        }
        else
        {
            _logger.LogInformation(
                "Streaming client archive directly to remote volume {Volume} for stack {StackId}.",
                volumeName,
                stackId);
            await _remoteEngine.SeedVolumeFromArchiveStreamAsync(
                stack, volumeName, preparedStream, cancellationToken, clearExisting: false);
            await PromoteClientRootInVolumeAsync(stack, volumeName, cancellationToken);
            await PurgePreservedPlatformContentFromVolumeAsync(stack, volumeName, cancellationToken);
            var streamed = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, volumeName, cancellationToken);
            if (!streamed.HasWowExe && !streamed.HasDataMpq)
            {
                throw new InvalidOperationException(
                    "The archive does not look like a WoW client (no Wow.exe or Data/*.MPQ found).");
            }
        }

        TryDeleteLegacyGameMirror(stackId);
        _logger.LogInformation("Base client for stack {StackId} installed in volume {Volume}.", stackId, volumeName);
        return await FinalizeContentChangeAsync(stackId, stack, volumeName, cancellationToken);
    }

    private async Task<ClientBaseInfoDto> UploadBaseClientLocallyAsync(
        string stackId,
        ManagedStackEntity stack,
        string volumeName,
        Stream archiveStream,
        CancellationToken cancellationToken)
    {
        CleanupUploadStaging(stackId, stagingDir: null);
        var stagingDir = CreateUploadStagingDir(stackId);
        var tempArchive = Path.Combine(stagingDir, "upload.archive");
        var tempExtract = Path.Combine(stagingDir, "extract");
        try
        {
            await using (var file = new FileStream(tempArchive, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await archiveStream.CopyToAsync(file, cancellationToken);
            }

            Directory.CreateDirectory(tempExtract);
            return await ExtractArchiveAndSeedVolumeAsync(
                stackId, stack, volumeName, tempArchive, tempExtract, cancellationToken);
        }
        finally
        {
            CleanupUploadStaging(stackId, stagingDir);
        }
    }

    private async Task<ClientBaseInfoDto> ExtractArchiveAndSeedVolumeAsync(
        string stackId,
        ManagedStackEntity stack,
        string volumeName,
        string archivePath,
        string extractDir,
        CancellationToken cancellationToken)
    {
        _clientJobs.ReportProgress(stackId, "Extracting the client archive…");
        _logger.LogInformation(
            "Extracting base client archive for stack {StackId} to {Dir}...",
            stackId,
            extractDir);
        ExtractArchive(archivePath, extractDir, cancellationToken, (files, bytes) =>
        {
            _clientJobs.ReportProgress(
                stackId,
                $"Extracting the archive… {files:N0} files ({FormatSize(bytes)})",
                bytes,
                bytesTotal: null);
        });
        TryDelete(archivePath, isDirectory: false);

        var clientRoot = FindClientRoot(extractDir)
            ?? throw new InvalidOperationException(
                "The uploaded archive does not look like a WoW client (no Wow.exe or Data/*.MPQ found).");

        var stripped = StripPreservedPlatformContent(clientRoot);
        if (stripped > 0)
        {
            _logger.LogInformation(
                "Skipped {Count} platform-managed file(s) from the uploaded client for stack {StackId} (letter patches and addons stay on the overlay).",
                stripped,
                stackId);
        }

        var totalBytes = DirectorySizeBytes(clientRoot);
        _clientJobs.ReportProgress(stackId, "Copying the client into the volume…");
        await SeedVolumeWithProgressAsync(stackId, stack, volumeName, clientRoot, totalBytes, cancellationToken);
        await PurgePreservedPlatformContentFromVolumeAsync(stack, volumeName, cancellationToken);
        await PurgeVolumeLeftoversAsync(
            stackId, stack, volumeName, EnumerateRelativeFiles(clientRoot), cancellationToken);
        TryDelete(extractDir, isDirectory: true);
        TryDeleteLegacyGameMirror(stackId);

        _logger.LogInformation("Base client for stack {StackId} installed in volume {Volume}.", stackId, volumeName);
        return await FinalizeContentChangeAsync(stackId, stack, volumeName, cancellationToken);
    }

    private async Task SeedVolumeWithProgressAsync(
        string stackId,
        ManagedStackEntity stack,
        string volumeName,
        string clientRoot,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        _clientJobs.ReportProgress(
            stackId,
            $"Copying {FormatSize(totalBytes)} into the volume…",
            0,
            totalBytes);

        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var poll = Task.Run(async () =>
        {
            while (!pollCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), pollCts.Token);
                    var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, volumeName, pollCts.Token);
                    var copied = Math.Clamp(summary.TotalBytes, 0, totalBytes > 0 ? totalBytes : summary.TotalBytes);
                    _clientJobs.ReportProgress(
                        stackId,
                        $"Copying into the volume… {FormatSize(copied)} / {FormatSize(totalBytes)}",
                        copied,
                        totalBytes);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // Best-effort; the copy itself is what matters.
                }
            }
        }, pollCts.Token);

        try
        {
            await _remoteEngine.SeedVolumeAsync(stack, volumeName, clientRoot, cancellationToken);
        }
        finally
        {
            pollCts.Cancel();
            try
            {
                await poll;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Streams a RAR/7z archive into a remote volume entry by entry, for external stacks whose engine
    /// cannot open the format.
    ///
    /// Unlike every other install path this one does not purge leftovers. The entry keys we write are
    /// the archive's own paths, but <see cref="PromoteClientRootInVolumeAsync"/> may then hoist the tree
    /// out of a wrapper folder, at which point those keys no longer describe where the files ended up —
    /// purging against them could delete the entire client. Leaving stale files behind is recoverable;
    /// emptying the volume is not.
    /// </summary>
    private async Task UploadBaseClientByEntryStreamingAsync(
        ManagedStackEntity stack,
        string volumeName,
        Stream archiveStream,
        CancellationToken cancellationToken)
    {
        IReadOnlySet<string> written;
        if (IsSevenZipStream(archiveStream))
        {
            using var archive = ArchiveFactory.OpenArchive(archiveStream);
            using var reader = archive.ExtractAllEntries();
            written = await StreamArchiveEntriesAsync(stack, volumeName, reader, cancellationToken);
        }
        else
        {
            using var reader = ReaderFactory.OpenReader(archiveStream);
            written = await StreamArchiveEntriesAsync(stack, volumeName, reader, cancellationToken);
        }

        await PromoteClientRootInVolumeAsync(stack, volumeName, cancellationToken);
        await PurgePreservedPlatformContentFromVolumeAsync(stack, volumeName, cancellationToken);

        var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, volumeName, cancellationToken);
        if (!summary.HasWowExe && !summary.HasDataMpq)
        {
            throw new InvalidOperationException(
                "The uploaded archive does not look like a WoW client (no Wow.exe or Data/*.MPQ found).");
        }

        if (summary.FileCount > written.Count)
        {
            _logger.LogWarning(
                "Streamed {Written} files into volume {Volume} but it holds {Total}. Entry-streamed archives "
                + "cannot be safely purged, so files from a previous upload may remain; delete them from the "
                + "client file browser if they are unwanted.",
                written.Count, volumeName, summary.FileCount);
        }
    }

    /// <summary>Streams every non-directory entry into the volume; returns the paths it wrote.</summary>
    private async Task<IReadOnlySet<string>> StreamArchiveEntriesAsync(
        ManagedStackEntity stack,
        string volumeName,
        IReader reader,
        CancellationToken cancellationToken)
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            var key = NormalizeArchiveEntryKey(reader.Entry.Key);
            EnsureEntryKeySafe(key);
            if (ClientBaseMergePolicy.ShouldPreservePlatformContent(key))
            {
                continue;
            }

            await using var entryStream = reader.OpenEntryStream();
            await _remoteEngine.WriteVolumeFileFromStreamAsync(stack, volumeName, key, entryStream, cancellationToken);
            written.Add(key);
        }

        return written;
    }

    private async Task PromoteClientRootInVolumeAsync(
        ManagedStackEntity stack,
        string volumeName,
        CancellationToken cancellationToken)
    {
        const string script = """
            is_strong() {
              local base="$1"
              if [ ! -f "$base/Wow.exe" ] && [ ! -f "$base/WoW.exe" ] && [ ! -f "$base/wow.exe" ]; then
                return 1
              fi
              ls "$base"/Data/*.MPQ "$base"/Data/*.mpq >/dev/null 2>&1
            }
            is_weak() {
              local base="$1"
              if [ -f "$base/Wow.exe" ] || [ -f "$base/WoW.exe" ] || [ -f "$base/wow.exe" ]; then
                return 0
              fi
              ls "$base"/Data/*.MPQ "$base"/Data/*.mpq >/dev/null 2>&1
            }
            find_root() {
              local base="$1"
              local depth="$2"
              if is_strong "$base"; then
                echo "$base"
                return 0
              fi
              if [ "$depth" -ge 8 ]; then
                return 1
              fi
              for child in "$base"/* "$base"/.[!.]*; do
                [ -d "$child" ] || continue
                name="${child##*/}"
                case "$name" in __MACOSX|.|..) continue ;; esac
                found=$(find_root "$child" $((depth + 1))) || true
                if [ -n "$found" ]; then
                  echo "$found"
                  return 0
                fi
              done
              return 1
            }
            find_weak() {
              local base="$1"
              local depth="$2"
              if is_weak "$base"; then
                echo "$base"
                return 0
              fi
              if [ "$depth" -ge 8 ]; then
                return 1
              fi
              for child in "$base"/* "$base"/.[!.]*; do
                [ -d "$child" ] || continue
                name="${child##*/}"
                case "$name" in __MACOSX|.|..) continue ;; esac
                found=$(find_weak "$child" $((depth + 1))) || true
                if [ -n "$found" ]; then
                  echo "$found"
                  return 0
                fi
              done
              return 1
            }
            ROOT=$(find_root . 0 || true)
            if [ -z "$ROOT" ]; then
              ROOT=$(find_weak . 0 || true)
            fi
            if [ -n "$ROOT" ] && [ "$ROOT" != "." ]; then
              for item in "$ROOT"/* "$ROOT"/.[!.]* "$ROOT"/..?*; do
                [ -e "$item" ] || continue
                name="${item##*/}"
                rm -rf "$name"
                mv "$item" .
              done
              rm -rf "$ROOT"
            fi
            """;

        await _remoteEngine.RunVolumeShellAsync(stack, volumeName, script, cancellationToken);
    }

    private static async Task<(Stream Stream, bool IsRar)> PrepareArchiveStreamAsync(
        Stream archiveStream,
        CancellationToken cancellationToken)
    {
        var header = new byte[8];
        var read = await archiveStream.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
        var isRar = read >= 4
            && header[0] == (byte)'R'
            && header[1] == (byte)'a'
            && header[2] == (byte)'r'
            && header[3] == (byte)'!';
        return (new PrefixStream(header.AsMemory(0, read), archiveStream), isRar);
    }

    private static bool IsSevenZipStream(Stream archiveStream)
    {
        if (!archiveStream.CanSeek)
        {
            return false;
        }

        Span<byte> sig = stackalloc byte[6];
        var position = archiveStream.Position;
        var read = archiveStream.Read(sig);
        archiveStream.Position = position;
        return read == 6
            && sig[0] == 0x37 && sig[1] == 0x7A && sig[2] == 0xBC
            && sig[3] == 0xAF && sig[4] == 0x27 && sig[5] == 0x1C;
    }

    private static string NormalizeArchiveEntryKey(string? entryKey)
        => (entryKey ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private static void EnsureEntryKeySafe(string key)
    {
        if (key.Length == 0 || key.Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Archive entry escapes the extraction directory: {key}");
        }
    }

    private sealed class PrefixStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
    {
        private int _prefixOffset;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixOffset < prefix.Length)
            {
                var fromPrefix = Math.Min(buffer.Length, prefix.Length - _prefixOffset);
                prefix.Span.Slice(_prefixOffset, fromPrefix).CopyTo(buffer.Span);
                _prefixOffset += fromPrefix;
                return fromPrefix;
            }

            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/').Trim());
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            throw new InvalidOperationException("A valid file name is required.");
        }
        return name;
    }

    private static string NormalizeRelative(string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : relativePath.Replace('\\', '/').Trim('/');

    private static void ValidateVolumeRelative(string normalizedRelative)
    {
        if (normalizedRelative.Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Invalid path.");
        }
    }

    private static string CombineRelative(string parent, string name)
        => string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";

    private static void ExtractArchive(
        string archivePath,
        string destination,
        CancellationToken cancellationToken,
        Action<int, long>? onProgress = null)
    {
        try
        {
            if (IsSevenZip(archivePath))
            {
                using var archive = ArchiveFactory.OpenArchive(new FileInfo(archivePath));
                using var reader = archive.ExtractAllEntries();
                ExtractEntries(reader, destination, cancellationToken, onProgress);
            }
            else
            {
                using var stream = File.OpenRead(archivePath);
                using var reader = ReaderFactory.OpenReader(stream);
                ExtractEntries(reader, destination, cancellationToken, onProgress);
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"The uploaded file could not be extracted ({ex.Message}). Supported formats are zip, rar, 7z, and tar (optionally gzip/bzip2/xz compressed).",
                ex);
        }
    }

    private static void ExtractEntries(
        IReader reader,
        string destination,
        CancellationToken cancellationToken,
        Action<int, long>? onProgress)
    {
        var options = new ExtractionOptions
        {
            ExtractFullPath = true,
            Overwrite = true,
            PreserveFileTime = false,
        };
        var files = 0;
        long bytes = 0;
        var lastReport = DateTime.UtcNow;
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            EnsureEntryWithinDestination(destination, reader.Entry.Key);
            reader.WriteEntryToDirectory(destination, options);
            files++;
            if (reader.Entry.Size > 0)
            {
                bytes += reader.Entry.Size;
            }

            if (onProgress is not null && (DateTime.UtcNow - lastReport) >= TimeSpan.FromMilliseconds(500))
            {
                onProgress(files, bytes);
                lastReport = DateTime.UtcNow;
            }
        }

        onProgress?.Invoke(files, bytes);
    }

    private static bool IsSevenZip(string archivePath)
    {
        try
        {
            Span<byte> sig = stackalloc byte[6];
            using var fs = File.OpenRead(archivePath);
            return fs.Read(sig) == 6
                && sig[0] == 0x37 && sig[1] == 0x7A && sig[2] == 0xBC
                && sig[3] == 0xAF && sig[4] == 0x27 && sig[5] == 0x1C;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureEntryWithinDestination(string destination, string? entryKey)
    {
        var key = (entryKey ?? string.Empty).Replace('\\', '/').TrimStart('/');
        if (key.Length == 0)
        {
            throw new InvalidOperationException("The archive contains an entry with an empty path.");
        }

        var destFull = Path.GetFullPath(destination);
        var destWithSep = destFull.EndsWith(Path.DirectorySeparatorChar)
            ? destFull
            : destFull + Path.DirectorySeparatorChar;

        var target = Path.GetFullPath(Path.Combine(destFull, key));
        if (target != destFull && !target.StartsWith(destWithSep, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Archive entry escapes the extraction directory: {entryKey}");
        }
    }

    private static string? FindClientRoot(string extractedRoot)
    {
        string? weak = null;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((extractedRoot, 0));

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            var hasExe = HasWowExeAt(dir);
            var hasMpq = HasDataMpqAt(dir);
            if (hasExe && hasMpq)
            {
                return dir;
            }

            if (weak is null && (hasExe || hasMpq))
            {
                weak = dir;
            }

            if (depth >= 8)
            {
                continue;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(dir);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (string.Equals(name, "__MACOSX", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                queue.Enqueue((child, depth + 1));
            }
        }

        return weak;
    }

    private static bool HasWowExeAt(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.exe")
                .Any(file => string.Equals(Path.GetFileName(file), "Wow.exe", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool HasDataMpqAt(string dir)
    {
        try
        {
            var dataDir = Directory.EnumerateDirectories(dir)
                .FirstOrDefault(item => string.Equals(Path.GetFileName(item), "Data", StringComparison.OrdinalIgnoreCase));
            if (dataDir is null)
            {
                return false;
            }

            return Directory.EnumerateFiles(dataDir)
                .Any(file => file.EndsWith(".MPQ", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private ClientBaseInfoDto BuildInfo(string stackId, VolumeTreeSummary summary)
    {
        var volumeName = ClientBaseVolume(stackId);
        var exists = summary.HasWowExe || summary.HasDataMpq || summary.FileCount > 0;
        string? inspectionWarning = null;
        if (summary.InspectionFailed)
        {
            inspectionWarning = string.IsNullOrWhiteSpace(summary.InspectionError)
                ? "The client-base volume exists but the manager could not read its contents. Check that the stack's Docker engine is reachable."
                : summary.InspectionError.Trim();
        }
        else if (summary.VolumeExists && !exists)
        {
            inspectionWarning =
                "The client-base Docker volume exists on the stack engine but appears empty. Re-upload the client if you removed it intentionally.";
        }

        return ApplyDownloadAvailability(new ClientBaseInfoDto
        {
            GamePath = $"docker://{volumeName}",
            Exists = exists,
            VolumeExists = summary.VolumeExists,
            InspectionWarning = inspectionWarning,
            FileCount = summary.FileCount,
            TotalSize = summary.TotalBytes,
            HasWowExe = summary.HasWowExe,
            HasDataMpq = summary.HasDataMpq,
        });
    }

    private ClientBaseInfoDto ApplyDownloadAvailability(ClientBaseInfoDto info)
    {
        var url = (_downloadOptions.BaseClientUrl ?? string.Empty).Trim();
        info.DownloadAvailable = !string.IsNullOrWhiteSpace(url);
        info.DownloadUnavailableReason = info.DownloadAvailable
            ? null
            : "No base-client download URL is configured yet.";
        return info;
    }

    /// <summary>
    /// Removes letter-patch MPQs and AddOns from an extracted client so a merge seed cannot
    /// overwrite platform-managed overlay content.
    /// </summary>
    internal static int StripPreservedPlatformContent(string clientRoot)
    {
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(clientRoot, "*", SearchOption.AllDirectories).ToList())
        {
            var relative = Path.GetRelativePath(clientRoot, file).Replace('\\', '/');
            if (!ClientBaseMergePolicy.ShouldPreservePlatformContent(relative))
            {
                continue;
            }

            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                removed++;
            }
            catch
            {
                // Best-effort: SeedVolume is still additive and the overlay wins at serve time.
            }
        }

        return removed;
    }

    private async Task PurgePreservedPlatformContentFromVolumeAsync(
        ManagedStackEntity stack,
        string volumeName,
        CancellationToken cancellationToken)
    {
        const string script = """
            rm -rf /dest/Interface/AddOns /dest/Interface/addons
            for f in /dest/Data/* /dest/data/*; do
              [ -f "$f" ] || continue
              name="${f##*/}"
              lower=$(printf '%s' "$name" | tr 'A-Z' 'a-z')
              case "$lower" in
                patch-[a-z].mpq) rm -f "$f" ;;
              esac
            done
            """;
        try
        {
            await _remoteEngine.RunVolumeShellAsync(stack, volumeName, script, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not purge platform-managed files from volume {Volume}.", volumeName);
        }
    }

    private string StackUploadStagingRoot(string stackId) => _options.UploadStagingRoot(stackId);

    private string CreateUploadStagingDir(string stackId)
    {
        var dir = Path.Combine(StackUploadStagingRoot(stackId), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static long DirectorySizeBytes(string root)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
                // File vanished while we were summing; ignore.
            }
        }

        return total;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var value = bytes / 1024d;
        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.00} {units[unit]}";
    }

    /// <summary>
    /// Removes the current staging tree and any leftover upload-staging for this stack from a
    /// previous failed upload so the zip and extracted files do not sit next to the volume copy.
    /// </summary>
    private void CleanupUploadStaging(string stackId, string? stagingDir)
    {
        TryDelete(Path.Combine(Path.GetTempPath(), "azp-client-upload", stackId), isDirectory: true);
        if (!string.IsNullOrWhiteSpace(stagingDir))
        {
            TryDelete(stagingDir, isDirectory: true);
            var root = StackUploadStagingRoot(stackId);
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
            {
                TryDelete(root, isDirectory: true);
            }

            return;
        }

        TryDelete(StackUploadStagingRoot(stackId), isDirectory: true);
    }

    /// <summary>
    /// Older uploads also mirrored the client under <c>Client:RootPath/stacks/{id}/game</c>. After the
    /// volume is populated that copy is leftover duplicate data.
    /// </summary>
    private void TryDeleteLegacyGameMirror(string stackId)
    {
        var gameDir = _options.StackGameDir(stackId);
        if (!Directory.Exists(gameDir))
        {
            return;
        }

        _logger.LogInformation(
            "Removing leftover manager client mirror at {Path} for stack {StackId} after the volume was seeded.",
            gameDir,
            stackId);
        TryDelete(gameDir, isDirectory: true);
    }

    private void TryDelete(string path, bool isDirectory)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (isDirectory)
                {
                    if (!Directory.Exists(path))
                    {
                        return;
                    }

                    ClearReadOnlyAttributes(path);
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }

                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                _logger.LogDebug(ex, "Retrying cleanup of {Path} (attempt {Attempt}).", path, attempt);
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up client upload path {Path}.", path);
            }
        }
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            try
            {
                info.Attributes = FileAttributes.Normal;
            }
            catch
            {
                // Best-effort: Directory.Delete still runs.
            }
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Lists and cleans per-stack Docker images, named volumes, and on-disk build checkouts.
/// </summary>
public sealed class StackDockerService : IStackDockerService
{
    private readonly AzerothCoreDbContext _dbContext;
    private readonly IDockerService _dockerService;
    private readonly IBuildService _buildService;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly IArmoryImageService _armoryImageService;
    private readonly ClientDistributionOptions _clientOptions;
    private readonly ArmoryAssetsOptions _armoryAssetsOptions;
    private readonly string _buildsPath;
    private readonly ILogger<StackDockerService> _logger;

    public StackDockerService(
        AzerothCoreDbContext dbContext,
        IDockerService dockerService,
        IBuildService buildService,
        IRemoteEngineService remoteEngine,
        IArmoryImageService armoryImageService,
        IOptions<DockerOptions> dockerOptions,
        IOptions<ClientDistributionOptions> clientOptions,
        IOptions<ArmoryAssetsOptions> armoryAssetsOptions,
        ILogger<StackDockerService> logger)
    {
        _dbContext = dbContext;
        _dockerService = dockerService;
        _buildService = buildService;
        _remoteEngine = remoteEngine;
        _armoryImageService = armoryImageService;
        _clientOptions = clientOptions.Value;
        _armoryAssetsOptions = armoryAssetsOptions.Value;
        _logger = logger;

        var configuredPath = dockerOptions.Value.BuildsPath;
        _buildsPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath);
    }

    private const double DiskWarningThresholdPercent = 65.0;

    public async Task<DockerDiskUsageDto> GetDiskUsageAsync(CancellationToken cancellationToken = default)
    {
        var usage = new DockerDiskUsageDto();
        var (dfExit, dfOutput, dfErr) = await RunHostAsync("df -B1 --output=size,used,avail,pcent /", cancellationToken);
        if (dfExit == 0)
        {
            ParseHostDisk(dfOutput, usage);
        }
        else
        {
            _logger.LogWarning("Host df failed (exit {ExitCode}): {Stderr}", dfExit, dfErr);
        }

        if (usage.TotalBytes <= 0)
        {
            var (fallbackExit, fallbackOutput, fallbackErr) = await RunHostAsync("/usr/bin/df -k /", cancellationToken);
            if (fallbackExit == 0)
            {
                ParseHostDiskFallback(fallbackOutput, usage);
            }
            else
            {
                _logger.LogWarning("Host df fallback failed (exit {ExitCode}): {Stderr}", fallbackExit, fallbackErr);
            }
        }

        var (sysExit, sysOutput, sysErr) = await RunDockerAsync("system df", cancellationToken);
        if (sysExit == 0)
        {
            ParseDockerSystemDf(sysOutput, usage);
        }
        else
        {
            _logger.LogDebug("docker system df unavailable (exit {ExitCode}): {Stderr}", sysExit, sysErr);
        }

        usage.IsWarning = usage.UsedPercent >= DiskWarningThresholdPercent;
        return usage;
    }

    public async Task<StackDockerOverviewDto?> GetOverviewAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return null;
        }

        var dockerContext = await ResolveDockerContextAsync(stack, cancellationToken);
        var contextArg = ContextArg(dockerContext);
        var project = DockerComposeOverrideGenerator.GetComposeProjectName(stackId);
        var containers = await _dockerService.ListContainersAsync(project, dockerContext, cancellationToken);
        var hasContainers = containers.Count > 0;
        var buildInProgress = await IsBuildInProgressAsync(stackId, cancellationToken);
        var stackBusy = IsStackRuntimeBusy(stack.Status) || buildInProgress;

        var managedStackIds = await GetManagedStackIdsAsync(cancellationToken);
        var anyClientEnabled = await AnyManagedStackClientEnabledAsync(cancellationToken);
        var allContainerImageRefs = await GetAllContainerImageRefsAsync(contextArg, cancellationToken);
        var activeImageRefs = await GetActiveImageReferencesAsync(contextArg, project, cancellationToken);
        foreach (var reference in allContainerImageRefs)
        {
            activeImageRefs.Add(reference);
        }

        var volumeUsage = await GetVolumeUsageAsync(contextArg, cancellationToken);
        var diskUsage = await GetDiskUsageAsync(cancellationToken);

        var buildFiles = DescribeBuildFiles(stackId, stackBusy, hasContainers, buildInProgress);
        var images = await ListStackImagesAsync(
            stackId,
            contextArg,
            activeImageRefs,
            managedStackIds,
            anyClientEnabled,
            cancellationToken);
        var allPlatformImages = await ListAllPlatformImagesAsync(
            contextArg,
            activeImageRefs,
            managedStackIds,
            anyClientEnabled,
            cancellationToken);
        var currentIds = images.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unusedImages = allPlatformImages
            .Where(i => !currentIds.Contains(i.Id))
            .ToList();
        var obsoleteBuildDirs = await ListObsoleteBuildDirsAsync(managedStackIds, cancellationToken);
        var danglingImages = await ListDanglingImagesAsync(contextArg, cancellationToken);
        var volumes = await ListStackVolumesAsync(
            stackId,
            project,
            contextArg,
            volumeUsage,
            hasContainers,
            stackBusy,
            cancellationToken);

        var deletableUnusedImages = unusedImages.Where(i => !i.IsActive).ToList();
        var managedBuildDirs = ListManagedBuildDirs(managedStackIds);
        var allManagedVolumes = await ListAllManagedStackVolumesAsync(
            managedStackIds,
            contextArg,
            volumeUsage,
            cancellationToken);
        var listedReclaimableBytes = diskUsage.DockerBuildCacheReclaimableBytes
            + danglingImages.Sum(i => i.SizeBytes)
            + deletableUnusedImages.Sum(i => i.SizeBytes)
            + obsoleteBuildDirs.Sum(d => d.SizeBytes);

        return new StackDockerOverviewDto
        {
            DiskUsage = diskUsage,
            DiskUsageBreakdown = BuildDiskUsageBreakdown(
                diskUsage,
                allPlatformImages,
                danglingImages,
                deletableUnusedImages,
                managedBuildDirs,
                obsoleteBuildDirs,
                allManagedVolumes),
            ReclaimableBreakdown = new DockerReclaimableBreakdownDto
            {
                BuildCacheBytes = diskUsage.DockerBuildCacheReclaimableBytes,
                DanglingImageBytes = danglingImages.Sum(i => i.SizeBytes),
                DanglingImageCount = danglingImages.Count,
                UnusedTaggedImageBytes = deletableUnusedImages.Sum(i => i.SizeBytes),
                UnusedTaggedImageCount = deletableUnusedImages.Count,
                ObsoleteBuildDirBytes = obsoleteBuildDirs.Sum(d => d.SizeBytes),
                ObsoleteBuildDirCount = obsoleteBuildDirs.Count,
                EngineReclaimableBytes = listedReclaimableBytes,
                ListedReclaimableBytes = listedReclaimableBytes,
            },
            BuildFiles = buildFiles,
            Images = images,
            UnusedImages = unusedImages,
            DanglingImages = danglingImages,
            ObsoleteBuildDirs = obsoleteBuildDirs,
            Volumes = volumes,
            BuildCacheBytes = diskUsage.DockerBuildCacheBytes,
            ReclaimableBytes = listedReclaimableBytes,
            TotalBytes = (buildFiles?.SizeBytes ?? 0)
                + images.Sum(i => i.SizeBytes)
                + unusedImages.Sum(i => i.SizeBytes)
                + danglingImages.Sum(i => i.SizeBytes)
                + obsoleteBuildDirs.Sum(d => d.SizeBytes)
                + volumes.Where(v => v.SizeBytes.HasValue).Sum(v => v.SizeBytes!.Value),
        };
    }

    public async Task<DockerCleanupResultDto> CleanupOldBuildsAsync(CancellationToken cancellationToken = default)
    {
        var result = new DockerCleanupResultDto();
        var managedStackIds = await GetManagedStackIdsAsync(cancellationToken);
        var anyClientEnabled = await AnyManagedStackClientEnabledAsync(cancellationToken);
        var allContainerImageRefs = await GetAllContainerImageRefsAsync(string.Empty, cancellationToken);
        var before = await GetDiskUsageAsync(cancellationToken);

        var (danglingExit, _, danglingErr) = await RunDockerAsync("image prune -f", cancellationToken);
        if (danglingExit != 0)
        {
            _logger.LogWarning("docker image prune failed: {Err}", danglingErr);
        }

        var allPlatformImages = await ListAllPlatformImagesAsync(
            string.Empty,
            allContainerImageRefs,
            managedStackIds,
            anyClientEnabled,
            cancellationToken);
        foreach (var image in allPlatformImages.Where(i => !i.IsActive))
        {
            var (exitCode, _, stderr) = await RunDockerAsync($"rmi -f {image.Id}", cancellationToken);
            if (exitCode != 0)
            {
                _logger.LogWarning("Failed to remove image {Image}: {Err}", image.Reference, stderr);
                continue;
            }

            result.RemovedImages++;
            result.FreedBytes += image.SizeBytes;
        }

        if (Directory.Exists(_buildsPath))
        {
            foreach (var dir in Directory.GetDirectories(_buildsPath))
            {
                var dirId = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(dirId) || managedStackIds.Contains(dirId))
                {
                    continue;
                }

                try
                {
                    var size = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                    Directory.Delete(dir, recursive: true);
                    result.RemovedBuildDirs++;
                    result.FreedBytes += size;
                    _logger.LogInformation("Removed orphaned build directory {Path}", dir);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove orphaned build directory {Path}", dir);
                }
            }
        }

        var after = await GetDiskUsageAsync(cancellationToken);
        if (result.FreedBytes <= 0 && before.UsedBytes > after.UsedBytes)
        {
            result.FreedBytes = before.UsedBytes - after.UsedBytes;
        }

        result.Success = true;
        result.Message = result.RemovedImages + result.RemovedBuildDirs > 0
            ? $"Removed {result.RemovedImages} old build image(s) and {result.RemovedBuildDirs} orphaned build checkout(s)."
            : "No old build images or orphaned build checkouts were found.";
        return result;
    }

    public async Task<DockerCleanupResultDto> CleanupUnusedAsync(CancellationToken cancellationToken = default)
    {
        var result = new DockerCleanupResultDto();
        var before = await GetDiskUsageAsync(cancellationToken);

        var (builderExit, _, builderErr) = await RunDockerAsync("builder prune -af", cancellationToken);
        if (builderExit != 0)
        {
            _logger.LogWarning("docker builder prune failed: {Err}", builderErr);
        }

        var oldBuilds = await CleanupOldBuildsAsync(cancellationToken);
        result.RemovedImages = oldBuilds.RemovedImages;
        result.RemovedBuildDirs = oldBuilds.RemovedBuildDirs;
        result.FreedBytes = oldBuilds.FreedBytes;

        var after = await GetDiskUsageAsync(cancellationToken);
        if (result.FreedBytes <= 0 && before.ReclaimableBytes > 0)
        {
            result.FreedBytes = Math.Max(0, before.UsedBytes - after.UsedBytes);
        }

        result.Success = true;
        result.Message = result.RemovedImages + result.RemovedBuildDirs > 0
            ? $"Removed {result.RemovedImages} unused image(s) and {result.RemovedBuildDirs} orphaned build checkout(s). Build cache was pruned."
            : "No unused images or orphaned build checkouts were found. Build cache was pruned if present.";
        return result;
    }

    public async Task<StackDockerDeleteResultDto> DeleteBuildFilesAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var overview = await GetOverviewAsync(stackId, cancellationToken);
        if (overview?.BuildFiles is null || !overview.BuildFiles.Exists)
        {
            return new StackDockerDeleteResultDto { Success = true, Message = "No build files to remove." };
        }

        if (overview.BuildFiles.IsActive)
        {
            return new StackDockerDeleteResultDto
            {
                Success = false,
                Message = overview.BuildFiles.ActiveReason ?? "Build files are in use and cannot be deleted.",
            };
        }

        var freed = await _buildService.CleanupAsync(stackId, cancellationToken);
        return new StackDockerDeleteResultDto
        {
            Success = true,
            Message = "Build files removed.",
            FreedBytes = freed,
        };
    }

    public async Task<StackDockerDeleteResultDto> DeleteImageAsync(string stackId, string imageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageId))
        {
            return new StackDockerDeleteResultDto { Success = false, Message = "Image id is required." };
        }

        var overview = await GetOverviewAsync(stackId, cancellationToken);
        if (overview is null)
        {
            return new StackDockerDeleteResultDto { Success = false, Message = "Stack not found." };
        }

        var image = overview.Images.Concat(overview.UnusedImages).FirstOrDefault(i =>
            string.Equals(i.Id, imageId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(i.Reference, imageId, StringComparison.OrdinalIgnoreCase));
        if (image is null)
        {
            return new StackDockerDeleteResultDto { Success = false, Message = "Image was not found for this Docker engine." };
        }

        if (image.IsActive)
        {
            return new StackDockerDeleteResultDto
            {
                Success = false,
                Message = image.ActiveReason ?? "Image is in use and cannot be deleted.",
            };
        }

        var stack = await _dbContext.ManagedStacks.AsNoTracking().SingleAsync(s => s.Id == stackId, cancellationToken);
        var contextArg = ContextArg(await ResolveDockerContextAsync(stack, cancellationToken));
        var (exitCode, _, stderr) = await RunDockerAsync($"{contextArg}rmi -f {image.Id}", cancellationToken);
        if (exitCode != 0)
        {
            return new StackDockerDeleteResultDto
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(stderr) ? "Failed to remove image." : stderr.Trim(),
            };
        }

        _logger.LogInformation("Removed docker image {Image} for stack {StackId}", image.Reference, stackId);
        return new StackDockerDeleteResultDto
        {
            Success = true,
            Message = $"Removed image {image.Reference}.",
            FreedBytes = image.SizeBytes,
        };
    }

    public async Task<StackDockerDeleteResultDto> DeleteVolumeAsync(string stackId, string volumeName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(volumeName))
        {
            return new StackDockerDeleteResultDto { Success = false, Message = "Volume name is required." };
        }

        if (!IsStackVolumeName(stackId, volumeName))
        {
            return new StackDockerDeleteResultDto { Success = false, Message = "Volume does not belong to this stack." };
        }

        var overview = await GetOverviewAsync(stackId, cancellationToken);
        var volume = overview?.Volumes.FirstOrDefault(v =>
            string.Equals(v.Name, volumeName, StringComparison.OrdinalIgnoreCase));
        if (volume is null)
        {
            return new StackDockerDeleteResultDto { Success = false, Message = "Volume not found for this stack." };
        }

        if (volume.IsActive)
        {
            return new StackDockerDeleteResultDto
            {
                Success = false,
                Message = volume.ActiveReason ?? "Volume is in use and cannot be deleted.",
            };
        }

        var stack = await _dbContext.ManagedStacks.AsNoTracking().SingleAsync(s => s.Id == stackId, cancellationToken);
        var contextArg = ContextArg(await ResolveDockerContextAsync(stack, cancellationToken));
        var (exitCode, _, stderr) = await RunDockerAsync($"{contextArg}volume rm -f {volumeName}", cancellationToken);
        if (exitCode != 0)
        {
            return new StackDockerDeleteResultDto
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(stderr) ? "Failed to remove volume." : stderr.Trim(),
            };
        }

        _logger.LogInformation("Removed docker volume {Volume} for stack {StackId}", volumeName, stackId);
        return new StackDockerDeleteResultDto
        {
            Success = true,
            Message = $"Removed volume {volumeName}.",
            FreedBytes = volume.SizeBytes ?? 0,
        };
    }

    public async Task<DockerVolumeAuditDto?> GetVolumeAuditAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return null;
        }

        var managedStackIds = await GetManagedStackIdsAsync(cancellationToken);
        var dockerContext = await ResolveDockerContextAsync(stack, cancellationToken);
        var contextArg = ContextArg(dockerContext);
        var volumeUsage = await GetVolumeUsageAsync(contextArg, cancellationToken);
        var stackRoot = Path.Combine(_buildsPath, stackId);
        var audit = new DockerVolumeAuditDto { AuditedAt = DateTime.UtcNow };

        audit.DuplicateCopies.AddRange(await BuildDuplicateCopyReportAsync(stack, stackRoot, volumeUsage, cancellationToken));
        audit.OrphanVolumes.AddRange(await ListOrphanVolumesAsync(contextArg, managedStackIds, volumeUsage, cancellationToken));

        if (stack.ClientEnabled)
        {
            var overlayVolume = DockerComposeOverrideGenerator.ClientOverlayVolumeName(stackId);
            var overlayMirrorDir = MigrationLayout.ClientOverlayDir(stackRoot);
            var mirrorFiles = ListLocalOverlayFiles(overlayMirrorDir);
            var volumeFiles = await _remoteEngine.ListVolumeFilesAsync(stack, overlayVolume, cancellationToken);
            audit.StaleOverlayFiles.AddRange(
                FindStaleOverlayFiles(overlayVolume, mirrorFiles, volumeFiles));
            audit.DriftNotes.AddRange(
                FindOverlayDriftNotes(overlayMirrorDir, overlayVolume, mirrorFiles, volumeFiles));
        }

        audit.ReclaimableBytes = audit.OrphanVolumes.Where(v => v.IsSafeToDelete).Sum(v => v.SizeBytes ?? 0)
            + audit.StaleOverlayFiles.Where(f => f.IsSafeToDelete).Sum(f => f.SizeBytes);
        audit.ReclaimableItemCount = audit.OrphanVolumes.Count(v => v.IsSafeToDelete)
            + audit.StaleOverlayFiles.Count(f => f.IsSafeToDelete);

        return audit;
    }

    public async Task<DockerVolumeCleanupResultDto> CleanupVolumeAuditAsync(
        string stackId,
        DockerVolumeCleanupRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var audit = await GetVolumeAuditAsync(stackId, cancellationToken);
        if (audit is null)
        {
            return new DockerVolumeCleanupResultDto { Success = false, Message = "Stack not found." };
        }

        var stack = await _dbContext.ManagedStacks.AsNoTracking().SingleAsync(s => s.Id == stackId, cancellationToken);
        var result = new DockerVolumeCleanupResultDto();
        var overlayVolume = DockerComposeOverrideGenerator.ClientOverlayVolumeName(stackId);

        var allowedOrphans = audit.OrphanVolumes
            .Where(v => v.IsSafeToDelete)
            .Select(v => v.VolumeName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedStalePaths = audit.StaleOverlayFiles
            .Where(f => f.IsSafeToDelete)
            .Select(f => f.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var volumeName in (request.OrphanVolumeNames ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!allowedOrphans.Contains(volumeName))
            {
                return new DockerVolumeCleanupResultDto
                {
                    Success = false,
                    Message = $"Volume '{volumeName}' is not a confirmed-safe orphan and was not deleted.",
                };
            }

            var orphan = audit.OrphanVolumes.First(v =>
                string.Equals(v.VolumeName, volumeName, StringComparison.OrdinalIgnoreCase));
            await _remoteEngine.RemoveVolumeAsync(stack, volumeName, cancellationToken);

            result.DeletedVolumes++;
            result.FreedBytes += orphan.SizeBytes ?? 0;
            _logger.LogInformation("Removed orphan docker volume {Volume} via volume audit cleanup.", volumeName);
        }

        var stalePaths = (request.StaleOverlayPaths ?? [])
            .Select(p => p.Replace('\\', '/').Trim().Trim('/'))
            .Where(p => p.Length > 0 && !p.Split('/').Contains("..", StringComparer.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var pathsToDelete = stalePaths.Where(allowedStalePaths.Contains).ToList();
        if (stalePaths.Count > 0 && pathsToDelete.Count != stalePaths.Count)
        {
            return new DockerVolumeCleanupResultDto
            {
                Success = false,
                Message = "One or more overlay paths are not confirmed stale and were not deleted.",
            };
        }

        if (pathsToDelete.Count > 0)
        {
            await _remoteEngine.DeleteVolumePathsAsync(stack, overlayVolume, pathsToDelete, cancellationToken);
            foreach (var path in pathsToDelete)
            {
                var stale = audit.StaleOverlayFiles.First(f =>
                    string.Equals(f.RelativePath, path, StringComparison.OrdinalIgnoreCase));
                result.DeletedFiles++;
                result.FreedBytes += stale.SizeBytes;
            }

            _logger.LogInformation(
                "Removed {Count} stale overlay path(s) from volume {Volume} via volume audit cleanup.",
                pathsToDelete.Count,
                overlayVolume);
        }

        if (result.DeletedVolumes == 0 && result.DeletedFiles == 0)
        {
            result.Success = true;
            result.Message = "No cleanup items were selected.";
            return result;
        }

        result.Success = true;
        result.Message = $"Removed {result.DeletedVolumes} orphan volume(s) and {result.DeletedFiles} stale overlay file(s).";
        return result;
    }

    private async Task<List<DockerVolumeAuditDuplicateCopyDto>> BuildDuplicateCopyReportAsync(
        ManagedStackEntity stack,
        string stackRoot,
        Dictionary<string, (long? SizeBytes, int Links)> volumeUsage,
        CancellationToken cancellationToken)
    {
        var copies = new List<DockerVolumeAuditDuplicateCopyDto>();

        if (stack.ClientEnabled)
        {
            var gameDir = _clientOptions.StackGameDir(stack.Id);
            var baseVolume = DockerComposeOverrideGenerator.ClientBaseVolumeName(stack.Id);
            volumeUsage.TryGetValue(baseVolume, out var baseUsage);
            copies.Add(new DockerVolumeAuditDuplicateCopyDto
            {
                Label = "WoW base client",
                ManagerPath = gameDir,
                ManagerBytes = DirectorySize(gameDir),
                VolumeName = baseVolume,
                VolumeBytes = baseUsage.SizeBytes ?? 0,
                Detail = "The uploaded client exists on manager disk and in a Docker volume by design. Do not delete either copy unless you intend to re-upload or re-seed.",
            });

            var overlayDir = MigrationLayout.ClientOverlayDir(stackRoot);
            var overlayVolume = DockerComposeOverrideGenerator.ClientOverlayVolumeName(stack.Id);
            volumeUsage.TryGetValue(overlayVolume, out var overlayUsage);
            copies.Add(new DockerVolumeAuditDuplicateCopyDto
            {
                Label = "Client overlay (patches)",
                ManagerPath = overlayDir,
                ManagerBytes = DirectorySize(overlayDir),
                VolumeName = overlayVolume,
                VolumeBytes = overlayUsage.SizeBytes ?? 0,
                Detail = "The manager overlay mirror is the source of truth. The Docker volume should match; stale files may exist only in the volume.",
            });
        }

        var armoryDir = _armoryAssetsOptions.StackRootPath(stack.Id);
        var assetsVolume = DockerComposeOverrideGenerator.ArmoryAssetsVolumeName(stack.Id);
        if (await _remoteEngine.VolumeExistsAsync(stack, assetsVolume, cancellationToken))
        {
            volumeUsage.TryGetValue(assetsVolume, out var assetsUsage);
            copies.Add(new DockerVolumeAuditDuplicateCopyDto
            {
                Label = "Armory 3D assets",
                ManagerPath = armoryDir,
                ManagerBytes = DirectorySize(armoryDir),
                VolumeName = assetsVolume,
                VolumeBytes = assetsUsage.SizeBytes ?? 0,
                Detail = "Armory assets are seeded from manager storage into a Docker volume for serving.",
            });
        }

        return copies.Where(c => c.ManagerBytes > 0 || c.VolumeBytes > 0).ToList();
    }

    private async Task<List<DockerVolumeAuditOrphanVolumeDto>> ListOrphanVolumesAsync(
        string contextArg,
        HashSet<string> managedStackIds,
        Dictionary<string, (long? SizeBytes, int Links)> volumeUsage,
        CancellationToken cancellationToken)
    {
        var orphans = new List<DockerVolumeAuditOrphanVolumeDto>();
        var (exitCode, output, _) = await RunDockerAsync($"{contextArg}volume ls --format \"{{{{.Name}}}}\"", cancellationToken);
        if (exitCode != 0)
        {
            return orphans;
        }

        foreach (var volumeName in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!volumeName.StartsWith("acore-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var inferredStackId = InferStackIdFromVolumeName(volumeName);
            if (string.IsNullOrWhiteSpace(inferredStackId)
                || managedStackIds.Contains(inferredStackId))
            {
                continue;
            }

            volumeUsage.TryGetValue(volumeName, out var usage);
            var linkCount = usage.Links;
            if (linkCount <= 0)
            {
                var (containerExit, containerOutput, _) = await RunDockerAsync(
                    $"{contextArg}ps -a --filter volume={volumeName} -q",
                    cancellationToken);
                if (containerExit == 0)
                {
                    linkCount = containerOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                }
            }

            var isSafe = linkCount == 0;
            orphans.Add(new DockerVolumeAuditOrphanVolumeDto
            {
                VolumeName = volumeName,
                InferredStackId = inferredStackId,
                SizeBytes = usage.SizeBytes,
                LinkCount = linkCount,
                IsSafeToDelete = isSafe,
                Reason = isSafe
                    ? "Volume belongs to a stack that no longer exists and is not linked to any container."
                    : "Volume belongs to a deleted stack but is still linked to a container — stop/remove the container first.",
            });
        }

        return orphans
            .OrderByDescending(v => v.SizeBytes ?? 0)
            .ThenBy(v => v.VolumeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, long> ListLocalOverlayFiles(string overlayRootDir)
    {
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(overlayRootDir))
        {
            return files;
        }

        foreach (var absolutePath in Directory.EnumerateFiles(overlayRootDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(overlayRootDir, absolutePath).Replace('\\', '/');
            if (IsOverlayBookkeeping(relativePath))
            {
                continue;
            }

            files[relativePath] = new FileInfo(absolutePath).Length;
        }

        return files;
    }

    private static List<DockerVolumeAuditStaleFileDto> FindStaleOverlayFiles(
        string overlayVolume,
        Dictionary<string, long> mirrorFiles,
        IReadOnlyList<VolumeFileEntry> volumeFiles)
    {
        var stale = new List<DockerVolumeAuditStaleFileDto>();
        foreach (var file in volumeFiles)
        {
            var relativePath = file.RelativePath.Replace('\\', '/').Trim().TrimStart('/');
            if (IsOverlayBookkeeping(relativePath))
            {
                continue;
            }

            if (mirrorFiles.ContainsKey(relativePath))
            {
                continue;
            }

            if (!IsManagedOverlayPath(relativePath))
            {
                stale.Add(new DockerVolumeAuditStaleFileDto
                {
                    VolumeName = overlayVolume,
                    RelativePath = relativePath,
                    SizeBytes = file.SizeBytes,
                    IsSafeToDelete = true,
                    Reason = "Present in the Docker overlay volume but outside managed overlay paths and not in the manager mirror.",
                });
                continue;
            }

            stale.Add(new DockerVolumeAuditStaleFileDto
            {
                VolumeName = overlayVolume,
                RelativePath = relativePath,
                SizeBytes = file.SizeBytes,
                IsSafeToDelete = true,
                Reason = "Present in the Docker overlay volume but not in the manager overlay mirror — not served to clients.",
            });
        }

        return stale.OrderByDescending(f => f.SizeBytes).ToList();
    }

    private static List<DockerVolumeAuditDriftNoteDto> FindOverlayDriftNotes(
        string overlayMirrorDir,
        string overlayVolume,
        Dictionary<string, long> mirrorFiles,
        IReadOnlyList<VolumeFileEntry> volumeFiles)
    {
        var notes = new List<DockerVolumeAuditDriftNoteDto>();
        var volumePaths = volumeFiles
            .Select(f => f.RelativePath.Replace('\\', '/').Trim().TrimStart('/'))
            .Where(p => !IsOverlayBookkeeping(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var mirrorOnly = mirrorFiles.Keys.Where(k => !volumePaths.Contains(k)).ToList();
        if (mirrorOnly.Count > 0)
        {
            var bytes = mirrorOnly.Sum(p => mirrorFiles[p]);
            notes.Add(new DockerVolumeAuditDriftNoteDto
            {
                Category = "Overlay mirror ahead of volume",
                Detail = $"{mirrorOnly.Count} file(s) ({FormatBytesShort(bytes)}) exist in {overlayMirrorDir} but not in {overlayVolume}. Start the stack or re-seed the overlay to sync — do not delete these from the mirror.",
            });
        }

        return notes;
    }

    private static bool IsManagedOverlayPath(string relativePath) =>
        relativePath.StartsWith("Data/", StringComparison.OrdinalIgnoreCase)
        || relativePath.StartsWith("Interface/AddOns/", StringComparison.OrdinalIgnoreCase);

    private static bool IsOverlayBookkeeping(string relativePath) =>
        relativePath is ".hashcache.json" or ".manifest.json" or ".verifytoken"
        || relativePath.EndsWith("/.hashcache.json", StringComparison.OrdinalIgnoreCase)
        || relativePath.EndsWith("/.manifest.json", StringComparison.OrdinalIgnoreCase)
        || relativePath.EndsWith("/.verifytoken", StringComparison.OrdinalIgnoreCase);

    private static string? InferStackIdFromVolumeName(string volumeName)
    {
        var match = Regex.Match(volumeName, @"^acore-([^_-]+)(?:-|_)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static long DirectorySize(string path) =>
        !Directory.Exists(path)
            ? 0
            : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    private static string FormatBytesShort(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var order = Math.Min((int)Math.Floor(Math.Log(bytes) / Math.Log(1024)), units.Length - 1);
        var size = bytes / Math.Pow(1024, order);
        return $"{size:0.#} {units[order]}";
    }

    private StackDockerBuildFilesDto DescribeBuildFiles(
        string stackId,
        bool stackBusy,
        bool hasContainers,
        bool buildInProgress)
    {
        var path = Path.Combine(_buildsPath, stackId);
        if (!Directory.Exists(path))
        {
            return new StackDockerBuildFilesDto { Exists = false, Path = path };
        }

        var size = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        string? reason = "Current stack build checkout.";
        if (buildInProgress)
        {
            reason = "A worldserver build is in progress.";
        }
        else if (stackBusy)
        {
            reason = "Stack is running or starting.";
        }
        else if (hasContainers)
        {
            reason = "Stack containers still exist (compose checkout is required to manage them).";
        }

        return new StackDockerBuildFilesDto
        {
            Exists = true,
            Path = path,
            SizeBytes = size,
            IsActive = true,
            ActiveReason = reason,
        };
    }

    private async Task<List<StackDockerImageDto>> ListStackImagesAsync(
        string stackId,
        string contextArg,
        HashSet<string> activeImageRefs,
        HashSet<string> managedStackIds,
        bool anyClientEnabled,
        CancellationToken cancellationToken)
    {
        var patterns = GetImagePatternsWithArmory(stackId);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var images = new List<StackDockerImageDto>();

        foreach (var pattern in patterns)
        {
            await CollectImagesAsync(
                contextArg,
                pattern,
                stackId,
                activeImageRefs,
                managedStackIds,
                anyClientEnabled,
                seen,
                images,
                currentStackOnly: true,
                cancellationToken);
        }

        return images
            .OrderByDescending(i => i.IsActive)
            .ThenBy(i => i.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<StackDockerImageDto>> ListAllPlatformImagesAsync(
        string contextArg,
        HashSet<string> activeImageRefs,
        HashSet<string> managedStackIds,
        bool anyClientEnabled,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var images = new List<StackDockerImageDto>();
        foreach (var pattern in new[] { "acore/*", "localhost/acore/*", "azeroth-platform*" })
        {
            await CollectImagesAsync(
                contextArg,
                pattern,
                ownerStackId: null,
                activeImageRefs,
                managedStackIds,
                anyClientEnabled,
                seen,
                images,
                currentStackOnly: false,
                cancellationToken);
        }

        return images
            .OrderByDescending(i => i.IsActive)
            .ThenBy(i => i.OwnerStackId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Repository, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task CollectImagesAsync(
        string contextArg,
        string pattern,
        string? ownerStackId,
        HashSet<string> activeImageRefs,
        HashSet<string> managedStackIds,
        bool anyClientEnabled,
        HashSet<string> seen,
        List<StackDockerImageDto> images,
        bool currentStackOnly,
        CancellationToken cancellationToken)
    {
        var (exitCode, output, _) = await RunDockerAsync(
            $"{contextArg}images --no-trunc --format \"{{{{json .}}}}\" {pattern}",
            cancellationToken);
        if (exitCode != 0)
        {
            return;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            DockerImageRow? row;
            try
            {
                row = JsonSerializer.Deserialize<DockerImageRow>(line);
            }
            catch
            {
                continue;
            }

            if (row is null || string.IsNullOrWhiteSpace(row.ID) || !seen.Add(row.ID))
            {
                continue;
            }

            var reference = BuildReference(row.Repository, row.Tag);
            var imageOwnerStackId = ResolveOwnerStackId(row.Repository, row.Tag);
            if (currentStackOnly
                && !string.Equals(imageOwnerStackId, ownerStackId, StringComparison.OrdinalIgnoreCase)
                && !IsCurrentStackArmoryImage(row.Repository, row.Tag, ownerStackId))
            {
                continue;
            }

            var sizeBytes = await GetImageSizeBytesAsync(contextArg, row.ID, cancellationToken)
                ?? ParseHumanSize(row.Size);
            var (isActive, reason) = ClassifyImage(
                reference,
                row.ID,
                row.Tag,
                imageOwnerStackId,
                activeImageRefs,
                managedStackIds,
                anyClientEnabled);

            images.Add(new StackDockerImageDto
            {
                Id = row.ID,
                Repository = row.Repository,
                Tag = row.Tag,
                Reference = reference,
                OwnerStackId = imageOwnerStackId,
                SizeBytes = sizeBytes,
                CreatedAt = ParseDockerCreatedAt(row.CreatedAt),
                IsActive = isActive,
                ActiveReason = reason,
            });
        }
    }

    private async Task<List<StackDockerImageDto>> ListDanglingImagesAsync(
        string contextArg,
        CancellationToken cancellationToken)
    {
        var images = new List<StackDockerImageDto>();
        var (exitCode, output, _) = await RunDockerAsync(
            $"{contextArg}images -f dangling=true --no-trunc --format \"{{{{json .}}}}\"",
            cancellationToken);
        if (exitCode != 0)
        {
            return images;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            DockerImageRow? row;
            try
            {
                row = JsonSerializer.Deserialize<DockerImageRow>(line);
            }
            catch
            {
                continue;
            }

            if (row is null || string.IsNullOrWhiteSpace(row.ID))
            {
                continue;
            }

            var sizeBytes = await GetImageSizeBytesAsync(contextArg, row.ID, cancellationToken)
                ?? ParseHumanSize(row.Size);
            images.Add(new StackDockerImageDto
            {
                Id = row.ID,
                Repository = string.IsNullOrWhiteSpace(row.Repository) ? "<none>" : row.Repository,
                Tag = string.IsNullOrWhiteSpace(row.Tag) ? "<none>" : row.Tag,
                Reference = BuildReference(row.Repository, row.Tag),
                SizeBytes = sizeBytes,
                CreatedAt = ParseDockerCreatedAt(row.CreatedAt),
                IsActive = false,
                ActiveReason = "Intermediate build layer (dangling).",
            });
        }

        return images
            .OrderByDescending(i => i.SizeBytes)
            .ThenByDescending(i => i.CreatedAt)
            .ToList();
    }

    private async Task<List<DockerObsoleteBuildDirDto>> ListObsoleteBuildDirsAsync(
        HashSet<string> managedStackIds,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        if (!Directory.Exists(_buildsPath))
        {
            return [];
        }

        var dirs = new List<DockerObsoleteBuildDirDto>();
        foreach (var dir in Directory.GetDirectories(_buildsPath))
        {
            var stackId = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(stackId) || managedStackIds.Contains(stackId))
            {
                continue;
            }

            dirs.Add(new DockerObsoleteBuildDirDto
            {
                StackId = stackId,
                Path = dir,
                SizeBytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length),
            });
        }

        return dirs.OrderByDescending(d => d.SizeBytes).ToList();
    }

    private List<DockerObsoleteBuildDirDto> ListManagedBuildDirs(HashSet<string> managedStackIds)
    {
        if (!Directory.Exists(_buildsPath))
        {
            return [];
        }

        var dirs = new List<DockerObsoleteBuildDirDto>();
        foreach (var dir in Directory.GetDirectories(_buildsPath))
        {
            var stackId = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(stackId) || !managedStackIds.Contains(stackId))
            {
                continue;
            }

            dirs.Add(new DockerObsoleteBuildDirDto
            {
                StackId = stackId,
                Path = dir,
                SizeBytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length),
            });
        }

        return dirs.OrderByDescending(d => d.SizeBytes).ToList();
    }

    private async Task<List<StackDockerVolumeDto>> ListAllManagedStackVolumesAsync(
        HashSet<string> managedStackIds,
        string contextArg,
        Dictionary<string, (long? SizeBytes, int Links)> volumeUsage,
        CancellationToken cancellationToken)
    {
        var volumes = new List<StackDockerVolumeDto>();
        foreach (var stackId in managedStackIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            var project = DockerComposeOverrideGenerator.GetComposeProjectName(stackId);
            var stackVolumes = await ListStackVolumesAsync(
                stackId,
                project,
                contextArg,
                volumeUsage,
                hasContainers: false,
                stackBusy: false,
                cancellationToken);
            volumes.AddRange(stackVolumes);
        }

        return volumes
            .OrderByDescending(v => v.SizeBytes ?? 0)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DockerDiskUsageBreakdownDto BuildDiskUsageBreakdown(
        DockerDiskUsageDto diskUsage,
        List<StackDockerImageDto> allPlatformImages,
        List<StackDockerImageDto> danglingImages,
        List<StackDockerImageDto> deletableUnusedImages,
        List<DockerObsoleteBuildDirDto> managedBuildDirs,
        List<DockerObsoleteBuildDirDto> obsoleteBuildDirs,
        List<StackDockerVolumeDto> allManagedVolumes)
    {
        var activeImages = allPlatformImages.Where(i => i.IsActive).ToList();
        var reclaimableImages = deletableUnusedImages;
        var reclaimableBytes = diskUsage.DockerBuildCacheReclaimableBytes
            + danglingImages.Sum(i => i.SizeBytes)
            + reclaimableImages.Sum(i => i.SizeBytes)
            + obsoleteBuildDirs.Sum(d => d.SizeBytes);

        return new DockerDiskUsageBreakdownDto
        {
            DockerImagesBytes = diskUsage.DockerImagesBytes,
            DockerImagesCount = allPlatformImages.Count + danglingImages.Count,
            ActiveImagesBytes = activeImages.Sum(i => i.SizeBytes),
            ActiveImagesCount = activeImages.Count,
            ReclaimableImagesBytes = reclaimableImages.Sum(i => i.SizeBytes) + danglingImages.Sum(i => i.SizeBytes),
            ReclaimableImagesCount = reclaimableImages.Count + danglingImages.Count,
            DockerVolumesBytes = diskUsage.DockerVolumesBytes,
            DockerVolumesCount = allManagedVolumes.Count,
            ActiveVolumesBytes = allManagedVolumes.Where(v => v.SizeBytes.HasValue).Sum(v => v.SizeBytes!.Value),
            ActiveVolumesCount = allManagedVolumes.Count,
            DockerBuildCacheBytes = diskUsage.DockerBuildCacheBytes,
            DockerContainersBytes = diskUsage.DockerContainersBytes,
            ManagedBuildCheckoutBytes = managedBuildDirs.Sum(d => d.SizeBytes),
            ManagedBuildCheckoutCount = managedBuildDirs.Count,
            OrphanedBuildCheckoutBytes = obsoleteBuildDirs.Sum(d => d.SizeBytes),
            OrphanedBuildCheckoutCount = obsoleteBuildDirs.Count,
            DanglingLayerBytes = danglingImages.Sum(i => i.SizeBytes),
            DanglingLayerCount = danglingImages.Count,
            ReclaimableBytes = reclaimableBytes,
            ActiveImages = activeImages,
            ActiveVolumes = allManagedVolumes,
        };
    }

    private async Task<HashSet<string>> GetManagedStackIdsAsync(CancellationToken cancellationToken) =>
        (await _dbContext.ManagedStacks
            .AsNoTracking()
            .Select(s => s.Id)
            .ToListAsync(cancellationToken))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<bool> AnyManagedStackClientEnabledAsync(CancellationToken cancellationToken) =>
        await _dbContext.ManagedStacks
            .AsNoTracking()
            .AnyAsync(s => s.ClientEnabled, cancellationToken);

    private async Task<HashSet<string>> GetAllContainerImageRefsAsync(
        string contextArg,
        CancellationToken cancellationToken)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (exitCode, output, _) = await RunDockerAsync(
            $"{contextArg}ps -a --format \"{{{{.Image}}}}\"",
            cancellationToken);
        if (exitCode != 0)
        {
            return refs;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            refs.Add(line);
        }

        return refs;
    }

    private static (bool IsActive, string? Reason) ClassifyImage(
        string reference,
        string imageId,
        string tag,
        string? ownerStackId,
        HashSet<string> activeImageRefs,
        HashSet<string> managedStackIds,
        bool anyClientEnabled)
    {
        var referencedByContainer = activeImageRefs.Contains(reference)
            || activeImageRefs.Contains(imageId)
            || activeImageRefs.Any(r => r.Contains(imageId, StringComparison.OrdinalIgnoreCase));
        if (referencedByContainer)
        {
            return (true, "Referenced by a container.");
        }

        if (tag is "<none>" or "")
        {
            return (false, "Dangling image layer.");
        }

        if (!string.IsNullOrWhiteSpace(ownerStackId) && managedStackIds.Contains(ownerStackId))
        {
            return (true, "Required image for a managed stack.");
        }

        if (IsSharedPlatformImage(reference))
        {
            if (reference.StartsWith("azeroth-platform-client:", StringComparison.OrdinalIgnoreCase)
                && anyClientEnabled
                && managedStackIds.Count > 0)
            {
                return (true, "Shared client distribution image required by managed stacks.");
            }

            if (reference.StartsWith("azeroth-platform:", StringComparison.OrdinalIgnoreCase)
                && !reference.StartsWith("azeroth-platform-armory-", StringComparison.OrdinalIgnoreCase)
                && managedStackIds.Count > 0)
            {
                return (true, "Shared platform image required by managed stacks.");
            }

            return (false, "Unused shared platform image.");
        }

        return (false, "Unused image from an old build or removed stack.");
    }

    private static bool IsSharedPlatformImage(string reference) =>
        reference.StartsWith("azeroth-platform:", StringComparison.OrdinalIgnoreCase)
        || reference.StartsWith("azeroth-platform-client:", StringComparison.OrdinalIgnoreCase);

    private static bool IsCurrentStackArmoryImage(string repository, string tag, string? stackId) =>
        !string.IsNullOrWhiteSpace(stackId)
        && repository.StartsWith("azeroth-platform-armory-", StringComparison.OrdinalIgnoreCase)
        && repository.EndsWith(stackId, StringComparison.OrdinalIgnoreCase);

    private static string? ResolveOwnerStackId(string repository, string tag)
    {
        if (tag is "<none>" or "" || tag.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            if (repository.StartsWith("azeroth-platform-armory-", StringComparison.OrdinalIgnoreCase))
            {
                const string prefix = "azeroth-platform-armory-";
                return repository[prefix.Length..];
            }

            return null;
        }

        if (tag.Length is >= 16 and <= 64 && Regex.IsMatch(tag, @"^[a-f0-9]+$", RegexOptions.IgnoreCase))
        {
            return tag;
        }

        return null;
    }

    private static void ParseHostDisk(string output, DockerDiskUsageDto usage)
    {
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
        {
            var parts = Regex.Split(raw.Trim(), @"\s+");
            if (parts.Length < 4)
            {
                continue;
            }

            if (long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var used)
                && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var available))
            {
                usage.TotalBytes = total;
                usage.UsedBytes = used;
                usage.AvailableBytes = available;
                usage.UsedPercent = total > 0 ? used * 100.0 / total : 0;
                if (parts[3].EndsWith('%'))
                {
                    if (double.TryParse(parts[3].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                    {
                        usage.UsedPercent = pct;
                    }
                }
            }

            break;
        }
    }

    private static void ParseHostDiskFallback(string output, DockerDiskUsageDto usage)
    {
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
        {
            var parts = Regex.Split(raw.Trim(), @"\s+");
            if (parts.Length < 5)
            {
                continue;
            }

            // df -k: Filesystem 1K-blocks Used Available Use% Mounted
            if (long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalK)
                && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var usedK)
                && long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var availK))
            {
                usage.TotalBytes = totalK * 1024L;
                usage.UsedBytes = usedK * 1024L;
                usage.AvailableBytes = availK * 1024L;
                usage.UsedPercent = usage.TotalBytes > 0 ? usage.UsedBytes * 100.0 / usage.TotalBytes : 0;
                if (parts[4].EndsWith('%')
                    && double.TryParse(parts[4].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                {
                    usage.UsedPercent = pct;
                }
            }

            break;
        }
    }

    private static void ParseDockerSystemDf(string output, DockerDiskUsageDto usage)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Images", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Regex.Split(line, @"\s{2,}");
                if (parts.Length >= 5)
                {
                    usage.DockerImagesBytes = ParseHumanSize(parts[3].Trim());
                    var reclaimable = ParseReclaimableSize(parts[4].Trim());
                    usage.DockerImagesReclaimableBytes = reclaimable;
                    usage.ReclaimableBytes += reclaimable;
                }
            }
            else if (line.StartsWith("Local Volumes", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Regex.Split(line, @"\s{2,}");
                if (parts.Length >= 5)
                {
                    usage.DockerVolumesBytes = ParseHumanSize(parts[3].Trim());
                    var reclaimable = ParseReclaimableSize(parts[4].Trim());
                    usage.DockerVolumesReclaimableBytes = reclaimable;
                    usage.ReclaimableBytes += reclaimable;
                }
            }
            else if (line.StartsWith("Containers", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Regex.Split(line, @"\s{2,}");
                if (parts.Length >= 5)
                {
                    usage.DockerContainersBytes = ParseHumanSize(parts[3].Trim());
                    var reclaimable = ParseReclaimableSize(parts[4].Trim());
                    usage.DockerContainersReclaimableBytes = reclaimable;
                    usage.ReclaimableBytes += reclaimable;
                }
            }
            else if (line.StartsWith("Build Cache", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Regex.Split(line, @"\s{2,}");
                if (parts.Length >= 5)
                {
                    usage.DockerBuildCacheBytes = ParseHumanSize(parts[3].Trim());
                    var reclaimable = ParseReclaimableSize(parts[4].Trim());
                    usage.DockerBuildCacheReclaimableBytes = reclaimable;
                    usage.ReclaimableBytes += reclaimable;
                }
            }
        }
    }

    private static long ParseReclaimableSize(string value)
    {
        var paren = value.IndexOf('(');
        var sizePart = paren > 0 ? value[..paren].Trim() : value.Trim();
        return ParseHumanSize(sizePart);
    }

    private async Task<List<StackDockerVolumeDto>> ListStackVolumesAsync(
        string stackId,
        string project,
        string contextArg,
        Dictionary<string, (long? SizeBytes, int Links)> volumeUsage,
        bool hasContainers,
        bool stackBusy,
        CancellationToken cancellationToken)
    {
        var names = GetExpectedVolumeNames(stackId, project);
        var volumes = new List<StackDockerVolumeDto>();

        foreach (var name in names)
        {
            var (existsExit, _, _) = await RunDockerAsync($"{contextArg}volume inspect {name}", cancellationToken);
            if (existsExit != 0)
            {
                continue;
            }

            volumeUsage.TryGetValue(name, out var usage);
            var (containerExit, containerOutput, _) = await RunDockerAsync(
                $"{contextArg}ps -a --filter volume={name} -q",
                cancellationToken);
            var linkCount = usage.Links > 0
                ? usage.Links
                : containerOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

            var active = true;
            string? reason = linkCount > 0
                ? "Mounted by one or more containers."
                : "Data volume for a managed stack.";

            volumes.Add(new StackDockerVolumeDto
            {
                Name = name,
                SizeBytes = usage.SizeBytes,
                LinkCount = linkCount,
                IsActive = active,
                ActiveReason = reason,
            });
        }

        return volumes
            .OrderByDescending(v => v.IsActive)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> GetImagePatterns(string stackId)
    {
        yield return $"acore/ac-wotlk-worldserver:{stackId}";
        yield return $"acore/ac-wotlk-authserver:{stackId}";
        yield return $"acore/ac-wotlk-db-import:{stackId}";
        yield return $"acore/ac-wotlk-client-data:{stackId}";
        yield return $"localhost/acore/ac-wotlk-worldserver:{stackId}";
        yield return $"localhost/acore/ac-wotlk-authserver:{stackId}";
        yield return $"localhost/acore/ac-wotlk-db-import:{stackId}";
        yield return $"localhost/acore/ac-wotlk-client-data:{stackId}";
    }

    private IEnumerable<string> GetImagePatternsWithArmory(string stackId)
    {
        foreach (var pattern in GetImagePatterns(stackId))
        {
            yield return pattern;
        }

        yield return _armoryImageService.ImageNameFor(stackId);
    }

    private async Task<HashSet<string>> GetActiveImageReferencesAsync(
        string contextArg,
        string project,
        CancellationToken cancellationToken)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (exitCode, output, _) = await RunDockerAsync(
            $"{contextArg}ps -a --filter label=com.docker.compose.project={project} --format \"{{{{.Image}}}}\"",
            cancellationToken);
        if (exitCode != 0)
        {
            return refs;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            refs.Add(line);
        }

        return refs;
    }

    private async Task<Dictionary<string, (long? SizeBytes, int Links)>> GetVolumeUsageAsync(
        string contextArg,
        CancellationToken cancellationToken)
    {
        var usage = new Dictionary<string, (long? SizeBytes, int Links)>(StringComparer.OrdinalIgnoreCase);
        var (exitCode, output, _) = await RunDockerAsync($"{contextArg}system df -v --format \"{{{{json .}}}}\"", cancellationToken);
        if (exitCode != 0)
        {
            // Older docker versions may not support --format on system df; fall back to plain parse.
            (exitCode, output, _) = await RunDockerAsync($"{contextArg}system df -v", cancellationToken);
            if (exitCode != 0)
            {
                return usage;
            }

            return ParseVolumeDfPlain(output);
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            DockerVolumeDfRow? row;
            try
            {
                row = JsonSerializer.Deserialize<DockerVolumeDfRow>(line);
            }
            catch
            {
                continue;
            }

            if (row is null || string.IsNullOrWhiteSpace(row.Name) || row.Type != "Local Volumes")
            {
                continue;
            }

            usage[row.Name] = (ParseHumanSize(row.Size), row.Links);
        }

        return usage;
    }

    private static Dictionary<string, (long? SizeBytes, int Links)> ParseVolumeDfPlain(string output)
    {
        var usage = new Dictionary<string, (long? SizeBytes, int Links)>(StringComparer.OrdinalIgnoreCase);
        var inVolumes = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Local Volumes space usage:", StringComparison.OrdinalIgnoreCase))
            {
                inVolumes = true;
                continue;
            }

            if (!inVolumes)
            {
                continue;
            }

            if (line.StartsWith("Build cache", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Images space", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Containers space", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (line.StartsWith("VOLUME NAME", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = Regex.Split(line, @"\s{2,}");
            if (parts.Length < 3)
            {
                continue;
            }

            var name = parts[0].Trim();
            _ = int.TryParse(parts[1].Trim(), out var links);
            usage[name] = (ParseHumanSize(parts[2].Trim()), links);
        }

        return usage;
    }

    private static List<string> GetExpectedVolumeNames(string stackId, string project) =>
        DockerComposeOverrideGenerator.GetAllStackVolumeNames(stackId).ToList();

    private static bool IsStackVolumeName(string stackId, string volumeName)
    {
        if (string.IsNullOrWhiteSpace(volumeName) || volumeName.Contains('/') || volumeName.Contains('\\'))
        {
            return false;
        }

        var project = DockerComposeOverrideGenerator.GetComposeProjectName(stackId);
        return volumeName.StartsWith($"acore-{stackId}", StringComparison.OrdinalIgnoreCase)
            || volumeName.StartsWith($"{project}_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStackRuntimeBusy(StackStatus status) =>
        status is StackStatus.Running
            or StackStatus.Starting
            or StackStatus.Building
            or StackStatus.Degraded
            or StackStatus.Initializing;

    private async Task<bool> IsBuildInProgressAsync(string stackId, CancellationToken cancellationToken)
    {
        var status = await _buildService.GetStatusAsync(stackId, cancellationToken);
        return status is not null
            && status.CurrentPhase is not (BuildPhase.Completed or BuildPhase.Failed);
    }

    private async Task<string?> ResolveDockerContextAsync(ManagedStackEntity stack, CancellationToken cancellationToken) =>
        stack.DeploymentTarget != DeploymentTarget.External
            ? null
            : await _remoteEngine.EnsureContextAsync(stack, cancellationToken);

    private static string ContextArg(string? dockerContext) =>
        string.IsNullOrWhiteSpace(dockerContext) ? string.Empty : $"--context {dockerContext} ";

    private async Task<long?> GetImageSizeBytesAsync(string contextArg, string imageId, CancellationToken cancellationToken)
    {
        var (exitCode, output, _) = await RunDockerAsync(
            $"{contextArg}image inspect -f \"{{{{.Size}}}}\" {imageId}",
            cancellationToken);
        if (exitCode != 0)
        {
            return null;
        }

        return long.TryParse(output.Trim(), out var size) ? size : null;
    }

    private static string BuildReference(string repository, string tag) =>
        tag is "<none>" or ""
            ? repository
            : $"{repository}:{tag}";

    private static long ParseHumanSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var match = Regex.Match(value.Trim(), @"^([\d.,]+)\s*([KMGT]?B)$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return 0;
        }

        if (!double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return 0;
        }

        var unit = match.Groups[2].Value.ToUpperInvariant();
        return unit switch
        {
            "KB" => (long)(amount * 1024),
            "MB" => (long)(amount * 1024 * 1024),
            "GB" => (long)(amount * 1024 * 1024 * 1024),
            "TB" => (long)(amount * 1024L * 1024 * 1024 * 1024),
            _ => (long)amount,
        };
    }

    private static DateTime? ParseDockerCreatedAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunHostAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(arguments);

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private sealed class DockerImageRow
    {
        public string ID { get; set; } = string.Empty;
        public string Repository { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
    }

    private sealed class DockerVolumeDfRow
    {
        public string Type { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
        public int Links { get; set; }
    }
}

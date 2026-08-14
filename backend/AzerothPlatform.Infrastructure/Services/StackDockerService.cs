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
    private readonly string? _managerDataVolumeName;
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
        _managerDataVolumeName = string.IsNullOrWhiteSpace(dockerOptions.Value.DataVolumeName)
            ? null
            : dockerOptions.Value.DataVolumeName.Trim();
        _buildsPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath);
        _managerDataRoot = Path.GetDirectoryName(_buildsPath.TrimEnd(Path.DirectorySeparatorChar)) ?? _buildsPath;
    }

    private readonly string _managerDataRoot;

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

    public async Task<DockerReclaimableBreakdownDto> GetReclaimableBreakdownAsync(CancellationToken cancellationToken = default)
    {
        var diskUsage = await GetDiskUsageAsync(cancellationToken);
        var managedStackIds = await GetManagedStackIdsAsync(cancellationToken);
        var anyClientEnabled = await AnyManagedStackClientEnabledAsync(cancellationToken);
        var activeImageRefs = await GetAllContainerImageRefsAsync(string.Empty, cancellationToken);
        var danglingImages = await ListDanglingImagesAsync(string.Empty, cancellationToken);
        var allPlatformImages = await ListAllPlatformImagesAsync(
            string.Empty,
            activeImageRefs,
            managedStackIds,
            anyClientEnabled,
            cancellationToken);
        var obsoleteBuildDirs = await ListObsoleteBuildDirsAsync(managedStackIds, cancellationToken);
        return BuildReclaimableBreakdown(diskUsage, danglingImages, allPlatformImages, obsoleteBuildDirs);
    }

    public async Task<DockerEngineOverviewDto> GetEngineOverviewAsync(CancellationToken cancellationToken = default)
    {
        var diskUsage = await GetDiskUsageAsync(cancellationToken);
        var reclaimableBreakdown = await GetReclaimableBreakdownAsync(cancellationToken);
        var managedStacks = await _dbContext.ManagedStacks
            .AsNoTracking()
            .Select(s => new { s.Id, s.StackName })
            .ToListAsync(cancellationToken);
        var managedStackIds = managedStacks.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stackNames = managedStacks.ToDictionary(s => s.Id, s => s.StackName, StringComparer.OrdinalIgnoreCase);
        var volumeUsage = await GetVolumeUsageAsync(string.Empty, cancellationToken);
        var anyClientEnabled = await AnyManagedStackClientEnabledAsync(cancellationToken);
        var activeImageRefs = await GetAllContainerImageRefsAsync(string.Empty, cancellationToken);

        var overview = new DockerEngineOverviewDto
        {
            DiskUsage = diskUsage,
            ReclaimableBreakdown = reclaimableBreakdown,
            ReclaimableBytes = reclaimableBreakdown.ListedReclaimableBytes,
        };
        overview.ManagerVolume = await GetManagerVolumeBreakdownAsync(cancellationToken);

        var volumeEntries = volumeUsage
            .Select(kvp => BuildEngineVolumeEntry(kvp.Key, kvp.Value.SizeBytes, kvp.Value.Links, managedStackIds))
            .OrderByDescending(v => v.SizeBytes ?? 0)
            .ToList();

        overview.VolumeGroups = GroupEngineVolumes(volumeEntries, stackNames);
        overview.TotalVolumeBytes = volumeEntries.Sum(v => v.SizeBytes ?? 0);
        overview.DeletableVolumeCount = volumeEntries.Count(v => v.IsDeletable);
        overview.DeletableVolumeBytes = volumeEntries.Where(v => v.IsDeletable).Sum(v => v.SizeBytes ?? 0);

        overview.Images = await ListEngineImagesAsync(managedStackIds, anyClientEnabled, activeImageRefs, cancellationToken);
        overview.TotalImageBytes = overview.Images.Sum(i => i.SizeBytes);

        return overview;
    }

    public async Task<StackDockerDeleteResultDto> DeleteEngineVolumeAsync(
        string volumeName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(volumeName))
        {
            return new StackDockerDeleteResultDto { Success = false, Message = "Volume name is required." };
        }

        var overview = await GetEngineOverviewAsync(cancellationToken);
        var volume = overview.VolumeGroups
            .SelectMany(g => g.Volumes)
            .FirstOrDefault(v => string.Equals(v.Name, volumeName, StringComparison.OrdinalIgnoreCase));
        if (volume is null)
        {
            return new StackDockerDeleteResultDto { Success = false, Message = "Volume was not found on the Docker engine." };
        }

        if (!volume.IsDeletable)
        {
            return new StackDockerDeleteResultDto
            {
                Success = false,
                Message = volume.Detail ?? "Volume is protected and cannot be deleted.",
            };
        }

        await _remoteEngine.RemoveLocalVolumeAsync(volumeName, cancellationToken);
        _logger.LogInformation("Removed engine volume {Volume} via global Docker admin.", volumeName);
        return new StackDockerDeleteResultDto
        {
            Success = true,
            Message = $"Removed volume {volumeName}.",
            FreedBytes = volume.SizeBytes ?? 0,
        };
    }

    public async Task<StackDockerDeleteResultDto> DeleteEngineImageAsync(
        string imageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageId))
        {
            return new StackDockerDeleteResultDto { Success = false, Message = "Image id is required." };
        }

        var overview = await GetEngineOverviewAsync(cancellationToken);
        var image = overview.Images.FirstOrDefault(i =>
            ImageIdsMatch(i.Id, imageId)
            || string.Equals(i.Reference, imageId, StringComparison.OrdinalIgnoreCase));
        if (image is null)
        {
            return new StackDockerDeleteResultDto { Success = false, Message = "Image was not found on the Docker engine." };
        }

        if (!image.IsDeletable)
        {
            return new StackDockerDeleteResultDto
            {
                Success = false,
                Message = "Image is protected and cannot be deleted.",
            };
        }

        var isDangling = string.Equals(image.Category, "Dangling", StringComparison.OrdinalIgnoreCase);
        var (exitCode, _, stderr) = await RunDockerAsync($"rmi -f {image.Id}", cancellationToken);
        if (exitCode != 0 && isDangling)
        {
            await RunDockerAsync("image prune -f", cancellationToken);
            (exitCode, _, stderr) = await RunDockerAsync($"rmi -f {image.Id}", cancellationToken);
        }
        if (exitCode != 0)
        {
            return new StackDockerDeleteResultDto
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(stderr) ? "Failed to remove image." : stderr.Trim(),
            };
        }

        return new StackDockerDeleteResultDto
        {
            Success = true,
            Message = isDangling
                ? $"Removed dangling build layer {ShortImageId(image.Id)}."
                : $"Removed image {image.Reference}.",
            FreedBytes = image.SizeBytes,
        };
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
        var isRemoteEngine = !string.IsNullOrWhiteSpace(contextArg);
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

        // Remote engines: `system df -v` and per-image inspects are very slow over SSH — skip or defer them.
        var volumeUsage = isRemoteEngine
            ? new Dictionary<string, (long? SizeBytes, int Links)>(StringComparer.OrdinalIgnoreCase)
            : await GetVolumeUsageAsync(contextArg, cancellationToken);
        var diskUsage = isRemoteEngine
            ? await GetRemoteEngineDiskUsageAsync(contextArg, cancellationToken)
            : await GetDiskUsageAsync(cancellationToken);

        var buildFiles = DescribeBuildFiles(stackId, stackBusy, hasContainers, buildInProgress);
        var images = await ListStackImagesAsync(
            stackId,
            contextArg,
            activeImageRefs,
            managedStackIds,
            anyClientEnabled,
            cancellationToken);
        List<StackDockerImageDto> allPlatformImages;
        List<StackDockerImageDto> unusedImages;
        List<DockerObsoleteBuildDirDto> obsoleteBuildDirs;
        List<StackDockerVolumeDto> allManagedVolumes;
        if (isRemoteEngine)
        {
            // The remote tab is scoped to this stack's engine — don't scan every managed stack id or the
            // whole remote daemon (that was dozens of SSH round trips and timed out the UI).
            allPlatformImages = images;
            unusedImages = [];
            obsoleteBuildDirs = [];
        }
        else
        {
            allPlatformImages = await ListAllPlatformImagesAsync(
                contextArg,
                activeImageRefs,
                managedStackIds,
                anyClientEnabled,
                cancellationToken);
            var currentIds = images.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            unusedImages = allPlatformImages
                .Where(i => !currentIds.Contains(i.Id))
                .ToList();
            obsoleteBuildDirs = await ListObsoleteBuildDirsAsync(managedStackIds, cancellationToken);
        }

        var danglingImages = await ListDanglingImagesAsync(contextArg, cancellationToken);
        var reclaimableBreakdown = BuildReclaimableBreakdown(
            diskUsage,
            danglingImages,
            allPlatformImages,
            obsoleteBuildDirs);
        var listedReclaimableBytes = reclaimableBreakdown.ListedReclaimableBytes;
        var deletableUnusedImages = unusedImages.Where(i => !i.IsActive).ToList();
        var managedBuildDirs = ListManagedBuildDirs(managedStackIds);
        var volumes = await ListStackVolumesAsync(
            stackId,
            project,
            contextArg,
            volumeUsage,
            hasContainers,
            stackBusy,
            cancellationToken);
        if (!isRemoteEngine)
        {
            allManagedVolumes = await ListAllManagedStackVolumesAsync(
                managedStackIds,
                contextArg,
                volumeUsage,
                cancellationToken);
        }
        else
        {
            allManagedVolumes = volumes;
        }

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
            ReclaimableBreakdown = reclaimableBreakdown,
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
        var before = await GetDiskUsageAsync(cancellationToken);

        foreach (var contextArg in await GetDistinctDockerContextArgsAsync(cancellationToken))
        {
            var engineResult = await CleanupOldBuildsOnEngineAsync(
                contextArg,
                managedStackIds,
                anyClientEnabled,
                cancellationToken);
            result.RemovedImages += engineResult.RemovedImages;
            result.FreedBytes += engineResult.FreedBytes;
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
            : result.FreedBytes > 0
                ? "Reclaimed dangling build layers from the Docker engine."
                : "No old build images or orphaned build checkouts were found.";
        return result;
    }

    public async Task<DockerCleanupResultDto> CleanupUnusedAsync(CancellationToken cancellationToken = default)
    {
        var result = new DockerCleanupResultDto();
        var before = await GetDiskUsageAsync(cancellationToken);
        var builderFreedBytes = 0L;

        foreach (var contextArg in await GetDistinctDockerContextArgsAsync(cancellationToken))
        {
            var (builderExit, builderOutput, builderErr) = await RunDockerAsync($"{contextArg}builder prune -af", cancellationToken);
            if (builderExit != 0)
            {
                _logger.LogWarning(
                    "docker builder prune failed{Context}: {Err}",
                    DescribeDockerContextArg(contextArg),
                    builderErr);
            }
            else
            {
                builderFreedBytes += ParseDockerReclaimedSpace(builderOutput);
            }
        }

        var oldBuilds = await CleanupOldBuildsAsync(cancellationToken);
        result.RemovedImages = oldBuilds.RemovedImages;
        result.RemovedBuildDirs = oldBuilds.RemovedBuildDirs;
        result.FreedBytes = builderFreedBytes + oldBuilds.FreedBytes;

        var after = await GetDiskUsageAsync(cancellationToken);
        if (result.FreedBytes <= 0 && before.UsedBytes > after.UsedBytes)
        {
            result.FreedBytes = Math.Max(0, before.UsedBytes - after.UsedBytes);
        }

        result.Success = true;
        result.Message = result.RemovedImages + result.RemovedBuildDirs > 0 || result.FreedBytes > 0
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

        var image = FindStackDockerImage(
            overview.Images.Concat(overview.UnusedImages).Concat(overview.DanglingImages),
            imageId);
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
        var isDangling = overview.DanglingImages.Any(d =>
            string.Equals(d.Id, image.Id, StringComparison.OrdinalIgnoreCase));
        var (exitCode, _, stderr) = await RunDockerAsync($"{contextArg}rmi -f {image.Id}", cancellationToken);
        if (exitCode != 0 && isDangling)
        {
            await RunDockerAsync($"{contextArg}image prune -f", cancellationToken);
            (exitCode, _, stderr) = await RunDockerAsync($"{contextArg}rmi -f {image.Id}", cancellationToken);
        }
        if (exitCode != 0)
        {
            return new StackDockerDeleteResultDto
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(stderr) ? "Failed to remove image." : stderr.Trim(),
            };
        }

        _logger.LogInformation("Removed docker image {Image} for stack {StackId}", image.Id, stackId);
        return new StackDockerDeleteResultDto
        {
            Success = true,
            Message = isDangling
                ? $"Removed dangling build layer {ShortImageId(image.Id)}."
                : $"Removed image {image.Reference}.",
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
                Detail = "Legacy manager client mirror. New uploads go directly to the stack client-base volume; safe to remove when the volume is populated.",
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
                Detail = "Legacy manager armory data mirror. New uploads go directly to the stack armory-assets volume.",
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

    private async Task<DockerManagerVolumeDto?> GetManagerVolumeBreakdownAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_managerDataVolumeName))
        {
            return null;
        }

        var (inspectExit, _, _) = await RunDockerAsync($"volume inspect {_managerDataVolumeName}", cancellationToken);
        if (inspectExit != 0)
        {
            return new DockerManagerVolumeDto
            {
                Name = _managerDataVolumeName,
                Detail = "Manager data volume was not found on the Docker engine.",
            };
        }

        var volumeUsage = await GetVolumeUsageAsync(string.Empty, cancellationToken);
        volumeUsage.TryGetValue(_managerDataVolumeName, out var usage);

        var command =
            $"docker run --rm -v {_managerDataVolumeName}:/data:ro alpine:3.20 " +
            "sh -c \"du -sb /data/* 2>/dev/null || true\"";
        var (exit, output, _) = await RunHostAsync(command, cancellationToken);

        var directories = new List<DockerVolumeDirectoryEntryDto>();
        if (exit == 0)
        {
            foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = raw.Split('\t', StringSplitOptions.TrimEntries);
                if (parts.Length < 2 || !long.TryParse(parts[0], out var sizeBytes))
                {
                    continue;
                }

                var name = Path.GetFileName(parts[1].Trim());
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                directories.Add(new DockerVolumeDirectoryEntryDto
                {
                    Name = name,
                    RelativePath = name,
                    SizeBytes = sizeBytes,
                    IsDeletable = await IsManagerPathDeletableAsync(name, cancellationToken),
                    Detail = await DescribeManagerDataDirectoryAsync(name, cancellationToken),
                });
            }
        }

        directories = directories.OrderByDescending(d => d.SizeBytes).ToList();
        return new DockerManagerVolumeDto
        {
            Name = _managerDataVolumeName,
            TotalBytes = usage.SizeBytes ?? directories.Sum(d => d.SizeBytes),
            IsProtected = true,
            Detail = "Platform database, build checkouts (/stacks), launcher builds, and optional legacy upload mirrors. New client/armory data uploads go directly to stack Docker volumes.",
            Directories = directories,
        };
    }

    private async Task<string?> DescribeManagerDataDirectoryAsync(string name, CancellationToken cancellationToken)
    {
        if (name.Equals("client", StringComparison.OrdinalIgnoreCase))
        {
            var (ready, blockers) = await EvaluateClientMirrorDeletionAsync("client", cancellationToken);
            if (!ready)
            {
                return "Legacy WoW client mirror on the manager. Do NOT delete until each stack's client-base Docker volume has the full client — use Migrate legacy client mirrors first. "
                    + string.Join("; ", blockers);
            }

            return "Legacy WoW client mirror (duplicate). Each stack's client-base volume is verified — safe to remove via Remove legacy stack mirrors.";
        }

        return DescribeManagerDataDirectory(name);
    }

    private static string? DescribeManagerDataDirectory(string name) => name switch
    {
        "client" => "Legacy WoW client mirror (duplicate). Safe to delete only when every stack's client-base Docker volume is verified.",
        "armory-assets" => "Armory files on manager: small styling/config plus optional legacy data mirrors under stacks/*/static/data.",
        "stacks" => "AzerothCore source checkouts for compiling (NOT Docker stack data). Required — do not delete.",
        "azeroth-platform.db" => "Platform SQLite database.",
        "launcher-dist" => "Built desktop launcher binaries staged for stacks.",
        "armory-build" => "Armory image build workspace (temporary).",
        "launcher-build" => "Launcher build workspace (temporary).",
        _ => null,
    };

    private static DockerEngineVolumeEntryDto BuildEngineVolumeEntry(
        string name,
        long? sizeBytes,
        int linkCount,
        HashSet<string> managedStackIds)
    {
        var inferredStackId = InferStackIdFromVolumeName(name);
        var isManager = IsManagerVolumeName(name);
        var isAnonymous = IsAnonymousVolumeName(name);
        var isManagedStack = !string.IsNullOrWhiteSpace(inferredStackId) && managedStackIds.Contains(inferredStackId);
        var isOrphanStack = !string.IsNullOrWhiteSpace(inferredStackId) && !managedStackIds.Contains(inferredStackId);

        string? detail;
        var isProtected = isManager || isManagedStack || linkCount > 0;
        var isDeletable = false;
        if (isManager)
        {
            detail = "Manager or platform infrastructure volume.";
        }
        else if (isManagedStack)
        {
            detail = linkCount > 0
                ? "Active stack data volume."
                : "Stack data volume (no containers currently linked).";
        }
        else if (isOrphanStack)
        {
            detail = linkCount > 0
                ? "Volume from a deleted stack still linked to a container — remove the container first."
                : "Orphan volume from a deleted stack.";
            isDeletable = linkCount == 0;
            isProtected = linkCount > 0;
        }
        else if (isAnonymous)
        {
            detail = linkCount > 0
                ? "Anonymous Docker volume in use."
                : "Unused anonymous volume (safe to remove).";
            isDeletable = linkCount == 0;
            isProtected = linkCount > 0;
        }
        else
        {
            detail = linkCount > 0
                ? "Named volume in use by another compose project or service."
                : "Unused named volume from another project (remove if not needed).";
            isDeletable = linkCount == 0;
            isProtected = linkCount > 0;
        }

        return new DockerEngineVolumeEntryDto
        {
            Name = name,
            SizeBytes = sizeBytes,
            LinkCount = linkCount,
            IsProtected = isProtected,
            IsDeletable = isDeletable,
            Detail = detail,
        };
    }

    private static List<DockerEngineVolumeGroupDto> GroupEngineVolumes(
        List<DockerEngineVolumeEntryDto> volumes,
        IReadOnlyDictionary<string, string> stackNames)
    {
        var groups = new List<DockerEngineVolumeGroupDto>();

        var manager = volumes.Where(v => IsManagerVolumeName(v.Name)).ToList();
        if (manager.Count > 0)
        {
            groups.Add(new DockerEngineVolumeGroupDto
            {
                Category = "Manager",
                TotalBytes = manager.Sum(v => v.SizeBytes ?? 0),
                Volumes = manager,
            });
        }

        foreach (var stackId in volumes
                     .Select(v => InferStackIdFromVolumeName(v.Name))
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            var stackVolumes = volumes
                .Where(v => string.Equals(InferStackIdFromVolumeName(v.Name), stackId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (stackVolumes.Count == 0)
            {
                continue;
            }

            var isManaged = stackNames.ContainsKey(stackId!);
            groups.Add(new DockerEngineVolumeGroupDto
            {
                Category = isManaged ? "Managed stack" : "Orphan stack",
                StackId = stackId,
                StackName = isManaged ? stackNames[stackId!] : null,
                TotalBytes = stackVolumes.Sum(v => v.SizeBytes ?? 0),
                Volumes = stackVolumes,
            });
        }

        var anonymous = volumes.Where(v => IsAnonymousVolumeName(v.Name)).ToList();
        if (anonymous.Count > 0)
        {
            groups.Add(new DockerEngineVolumeGroupDto
            {
                Category = "Anonymous",
                TotalBytes = anonymous.Sum(v => v.SizeBytes ?? 0),
                Volumes = anonymous,
            });
        }

        var other = volumes
            .Where(v => !IsManagerVolumeName(v.Name)
                && string.IsNullOrWhiteSpace(InferStackIdFromVolumeName(v.Name))
                && !IsAnonymousVolumeName(v.Name))
            .ToList();
        if (other.Count > 0)
        {
            groups.Add(new DockerEngineVolumeGroupDto
            {
                Category = "Other projects",
                TotalBytes = other.Sum(v => v.SizeBytes ?? 0),
                Volumes = other,
            });
        }

        return groups;
    }

    private async Task<List<DockerEngineImageDto>> ListEngineImagesAsync(
        HashSet<string> managedStackIds,
        bool anyClientEnabled,
        HashSet<string> activeImageRefs,
        CancellationToken cancellationToken)
    {
        var images = new List<DockerEngineImageDto>();
        var (exitCode, output, _) = await RunDockerAsync("images --no-trunc --format \"{{json .}}\"", cancellationToken);
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

            var reference = BuildReference(row.Repository, row.Tag);
            var sizeBytes = await GetImageSizeBytesAsync(string.Empty, row.ID, cancellationToken)
                ?? ParseHumanSize(row.Size);
            var ownerStackId = ResolveOwnerStackId(row.Repository, row.Tag);
            var containerCount = await GetImageContainerCountAsync(row.ID, cancellationToken);
            var referencedByContainer = activeImageRefs.Contains(reference)
                || activeImageRefs.Contains(row.ID)
                || activeImageRefs.Any(r => r.Contains(row.ID, StringComparison.OrdinalIgnoreCase));
            var category = ClassifyEngineImageCategory(reference, ownerStackId);
            var (isProtected, isDeletable) = ClassifyEngineImageDeletable(
                reference,
                row.Tag,
                ownerStackId,
                category,
                managedStackIds,
                anyClientEnabled,
                containerCount,
                referencedByContainer);

            images.Add(new DockerEngineImageDto
            {
                Id = row.ID,
                Reference = reference,
                SizeBytes = sizeBytes,
                Category = category,
                OwnerStackId = ownerStackId,
                ContainerCount = containerCount,
                IsProtected = isProtected,
                IsDeletable = isDeletable,
            });
        }

        return images
            .OrderByDescending(i => i.IsProtected)
            .ThenBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(i => i.SizeBytes)
            .ToList();
    }

    private async Task<int> GetImageContainerCountAsync(string imageId, CancellationToken cancellationToken)
    {
        var (exitCode, output, _) = await RunDockerAsync(
            $"ps -a --filter ancestor={imageId} -q",
            cancellationToken);
        return exitCode == 0
            ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            : 0;
    }

    private static string ClassifyEngineImageCategory(string reference, string? ownerStackId)
    {
        if (reference.StartsWith("azeroth-platform:", StringComparison.OrdinalIgnoreCase)
            && !reference.StartsWith("azeroth-platform-armory-", StringComparison.OrdinalIgnoreCase)
            && !reference.StartsWith("azeroth-platform-client:", StringComparison.OrdinalIgnoreCase))
        {
            return "Manager";
        }

        if (reference.StartsWith("azeroth-platform-client:", StringComparison.OrdinalIgnoreCase))
        {
            return "Shared client";
        }

        if (reference.StartsWith("caddy:", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("tecnativa/docker-socket-proxy:", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("moby/buildkit:", StringComparison.OrdinalIgnoreCase))
        {
            return "Manager infrastructure";
        }

        if (!string.IsNullOrWhiteSpace(ownerStackId)
            || reference.StartsWith("acore/", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("localhost/acore/", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("azeroth-platform-armory-", StringComparison.OrdinalIgnoreCase))
        {
            return "Stack";
        }

        if (reference.StartsWith("<none>", StringComparison.OrdinalIgnoreCase) || reference.Contains("<none>"))
        {
            return "Dangling";
        }

        return "Other";
    }

    private static (bool IsProtected, bool IsDeletable) ClassifyEngineImageDeletable(
        string reference,
        string tag,
        string? ownerStackId,
        string category,
        HashSet<string> managedStackIds,
        bool anyClientEnabled,
        int containerCount,
        bool referencedByContainer)
    {
        if (containerCount > 0 || referencedByContainer)
        {
            return (true, false);
        }

        if (category is "Manager" or "Manager infrastructure")
        {
            return (true, false);
        }

        if (category == "Shared client")
        {
            return (anyClientEnabled && managedStackIds.Count > 0, !anyClientEnabled);
        }

        if (category == "Stack" && !string.IsNullOrWhiteSpace(ownerStackId) && managedStackIds.Contains(ownerStackId))
        {
            return (true, false);
        }

        if (category == "Dangling" || tag is "<none>" or "")
        {
            return (false, true);
        }

        if (category == "Other" || category == "Stack")
        {
            return (containerCount > 0, containerCount == 0);
        }

        return (true, false);
    }

    public async Task<DockerManagerFilesDto> GetManagerFilesAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeManagerRelativePath(relativePath);
        var dto = new DockerManagerFilesDto { Path = normalized };
        var target = ResolveManagerPath(normalized);
        if (target is null || !Directory.Exists(target))
        {
            return dto;
        }

        dto.Exists = true;
        foreach (var dir in Directory.EnumerateDirectories(target))
        {
            var name = Path.GetFileName(dir);
            var rel = CombineManagerRelative(normalized, name);
            dto.Entries.Add(new DockerManagerFileEntryDto
            {
                Name = name,
                RelativePath = rel,
                IsDirectory = true,
                SizeBytes = 0,
                IsDeletable = await IsManagerPathDeletableAsync(rel, cancellationToken),
                Detail = DescribeManagerEntry(rel, isDirectory: true),
            });
        }

        foreach (var file in Directory.EnumerateFiles(target))
        {
            var name = Path.GetFileName(file);
            var rel = CombineManagerRelative(normalized, name);
            long size = 0;
            try { size = new FileInfo(file).Length; } catch { /* ignore */ }
            dto.Entries.Add(new DockerManagerFileEntryDto
            {
                Name = name,
                RelativePath = rel,
                IsDirectory = false,
                SizeBytes = size,
                IsDeletable = await IsManagerPathDeletableAsync(rel, cancellationToken),
                Detail = DescribeManagerEntry(rel, isDirectory: false),
            });
        }

        dto.Entries.Sort((a, b) =>
            a.IsDirectory != b.IsDirectory
                ? (a.IsDirectory ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return dto;
    }

    public async Task<StackDockerDeleteResultDto> DeleteManagerFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeManagerRelativePath(relativePath);
        if (!IsManagerPathDeletableCandidate(normalized))
        {
            return new StackDockerDeleteResultDto
            {
                Success = false,
                Message = "This manager data path is protected and cannot be deleted.",
            };
        }

        if (IsClientLegacyMirrorPath(normalized))
        {
            var (allowed, blockers) = await EvaluateClientMirrorDeletionAsync(normalized, cancellationToken);
            if (!allowed)
            {
                return new StackDockerDeleteResultDto
                {
                    Success = false,
                    Message = "Refusing to delete legacy client data on the manager: "
                        + string.Join("; ", blockers)
                        + " Upload the client on each stack's Client tab (or use Migrate legacy client mirrors) before removing the manager copy.",
                };
            }
        }

        var target = ResolveManagerPath(normalized);
        if (target is null || (!Directory.Exists(target) && !File.Exists(target)))
        {
            return new StackDockerDeleteResultDto
            {
                Success = false,
                Message = "Path was not found on the manager data volume.",
            };
        }

        long freed = 0;
        try
        {
            if (Directory.Exists(target))
            {
                freed = DirectorySize(target);
                Directory.Delete(target, recursive: true);
            }
            else
            {
                freed = new FileInfo(target).Length;
                File.Delete(target);
            }
        }
        catch (Exception ex)
        {
            return new StackDockerDeleteResultDto
            {
                Success = false,
                Message = $"Failed to delete: {ex.Message}",
            };
        }

        _logger.LogInformation("Removed manager data path {Path} ({Bytes} bytes).", normalized, freed);
        return new StackDockerDeleteResultDto
        {
            Success = true,
            Message = $"Removed {normalized}.",
            FreedBytes = freed,
        };
    }

    public async Task<DockerManagerMirrorCleanupResultDto> MigrateClientMirrorsToVolumesAsync(CancellationToken cancellationToken = default)
    {
        var result = new DockerManagerMirrorCleanupResultDto();
        var stacks = await _dbContext.ManagedStacks.AsNoTracking().Where(s => s.ClientEnabled).ToListAsync(cancellationToken);

        foreach (var stack in stacks)
        {
            var volumeName = DockerComposeOverrideGenerator.ClientBaseVolumeName(stack.Id);
            var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, volumeName, cancellationToken);
            if (ClientBaseVolumeLooksPopulated(summary))
            {
                continue;
            }

            var mirror = _clientOptions.StackGameDir(stack.Id);
            if (!Directory.Exists(mirror) || !LooksLikeWoWClientRoot(mirror))
            {
                result.RemovedLabels.Add($"No manager mirror to migrate ({stack.StackName})");
                continue;
            }

            try
            {
                await _remoteEngine.EnsureVolumeExistsAsync(stack, volumeName, cancellationToken);
                await _remoteEngine.ClearVolumeContentsAsync(stack, volumeName, cancellationToken);
                await _remoteEngine.SeedVolumeAsync(stack, volumeName, mirror, cancellationToken);
                result.RemovedPaths++;
                result.RemovedLabels.Add($"Migrated client mirror → {volumeName} ({stack.StackName})");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to migrate client mirror for stack {StackId}.", stack.Id);
                result.RemovedLabels.Add($"Migration failed ({stack.StackName}): {ex.Message}");
            }
        }

        result.Success = true;
        result.Message = result.RemovedPaths > 0
            ? $"Migrated {result.RemovedPaths} legacy client mirror(s) into stack Docker volumes. You can remove the manager client/ tree once every stack is verified."
            : result.RemovedLabels.Count > 0
                ? "No client mirrors were migrated. " + string.Join("; ", result.RemovedLabels)
                : "Every client-enabled stack already has a populated client-base volume.";
        return result;
    }

    public async Task<DockerManagerMirrorCleanupResultDto> CleanupManagerMirrorsAsync(CancellationToken cancellationToken = default)
    {
        var result = new DockerManagerMirrorCleanupResultDto();
        var stacks = await _dbContext.ManagedStacks.AsNoTracking().ToListAsync(cancellationToken);

        foreach (var stack in stacks)
        {
            if (stack.ClientEnabled)
            {
                var mirror = Path.Combine(_clientOptions.RootPath, "stacks", stack.Id, "game");
                if (Directory.Exists(mirror))
                {
                    var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(
                        stack,
                        DockerComposeOverrideGenerator.ClientBaseVolumeName(stack.Id),
                        cancellationToken);
                    if (ClientBaseVolumeLooksPopulated(summary))
                    {
                        var freed = DirectorySize(mirror);
                        Directory.Delete(mirror, recursive: true);
                        result.FreedBytes += freed;
                        result.RemovedPaths++;
                        result.RemovedLabels.Add($"Client mirror ({stack.StackName})");
                    }
                }
            }

            if (stack.ArmoryEnabled)
            {
                var dataMirror = _armoryAssetsOptions.DataPathFor(stack.Id);
                if (Directory.Exists(dataMirror))
                {
                    var volumeName = DockerComposeOverrideGenerator.ArmoryAssetsVolumeName(stack.Id);
                    if (await _remoteEngine.VolumeExistsAsync(stack, volumeName, cancellationToken))
                    {
                        var files = await _remoteEngine.ListVolumeFilesAsync(stack, volumeName, cancellationToken);
                        if (files.Count > 0)
                        {
                            var freed = DirectorySize(dataMirror);
                            Directory.Delete(dataMirror, recursive: true);
                            result.FreedBytes += freed;
                            result.RemovedPaths++;
                            result.RemovedLabels.Add($"Armory data mirror ({stack.StackName})");
                        }
                    }
                }
            }
        }

        // Remove entire legacy client/ tree when every client-enabled stack has files in its client-base volume.
        var clientRoot = _clientOptions.RootPath;
        if (Directory.Exists(clientRoot))
        {
            var clientStacks = stacks.Where(s => s.ClientEnabled).ToList();
            var allVolumesReady = clientStacks.Count == 0;
            if (clientStacks.Count > 0)
            {
                allVolumesReady = true;
                foreach (var stack in clientStacks)
                {
                    var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(
                        stack,
                        DockerComposeOverrideGenerator.ClientBaseVolumeName(stack.Id),
                        cancellationToken);
                    if (!ClientBaseVolumeLooksPopulated(summary))
                    {
                        allVolumesReady = false;
                        break;
                    }
                }
            }

            if (allVolumesReady && Directory.EnumerateFileSystemEntries(clientRoot).Any())
            {
                var freed = DirectorySize(clientRoot);
                Directory.Delete(clientRoot, recursive: true);
                result.FreedBytes += freed;
                result.RemovedPaths++;
                result.RemovedLabels.Add("Legacy client mirror (entire client/ tree)");
            }
        }

        result.Success = true;
        result.Message = result.RemovedPaths > 0
            ? $"Removed {result.RemovedPaths} legacy manager mirror(s). Freed about {FormatBytesShort(result.FreedBytes)}."
            : "No legacy manager mirrors were found (or stack volumes were not verified).";
        return result;
    }

    public Task<DockerPlatformKeysDto> GetPlatformKeysStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keys = new[]
        {
            ("secret-protection.key", "Encrypts external-stack SSH keys and other secrets at rest."),
            ("jwt-signing.key", "Signs admin login tokens. Regenerating it logs everyone out."),
            ("manifest-signing.key", "Signs client file manifests for the launcher. Regenerating it requires clients to re-fetch config."),
        };

        var dto = new DockerPlatformKeysDto
        {
            Detail = "These files live on the manager data volume. Do not prune this volume without backing them up.",
        };

        foreach (var (name, detail) in keys)
        {
            var path = Path.Combine(_managerDataRoot, name);
            dto.Keys.Add(new DockerPlatformKeyStatusDto
            {
                Name = name,
                Present = File.Exists(path),
                Detail = detail,
            });
        }

        return Task.FromResult(dto);
    }

    private static string NormalizeManagerRelativePath(string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : relativePath.Replace('\\', '/').Trim('/');

    private static string CombineManagerRelative(string parent, string name)
        => string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";

    private string? ResolveManagerPath(string normalizedRelative)
    {
        var root = Path.GetFullPath(_managerDataRoot);
        var combined = string.IsNullOrEmpty(normalizedRelative)
            ? root
            : Path.GetFullPath(Path.Combine(root, normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (combined != root && !combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        return combined;
    }

    private string? ToManagerRelative(string absolutePath)
    {
        var root = Path.GetFullPath(_managerDataRoot);
        var full = Path.GetFullPath(absolutePath);
        if (full == root)
        {
            return string.Empty;
        }

        var prefix = root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return full[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/');
    }

    private async Task<bool> IsManagerPathDeletableAsync(string normalizedRelative, CancellationToken cancellationToken)
    {
        if (!IsManagerPathDeletableCandidate(normalizedRelative))
        {
            return false;
        }

        if (IsClientLegacyMirrorPath(normalizedRelative))
        {
            var (allowed, _) = await EvaluateClientMirrorDeletionAsync(normalizedRelative, cancellationToken);
            return allowed;
        }

        return true;
    }

    private static bool IsManagerPathDeletableCandidate(string normalizedRelative)
    {
        if (string.IsNullOrEmpty(normalizedRelative))
        {
            return false;
        }

        var rel = normalizedRelative.Replace('\\', '/').Trim('/');
        if (rel.Equals("azeroth-platform.db", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!rel.Contains('/') && rel.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rel.StartsWith("stacks/", StringComparison.OrdinalIgnoreCase)
            || rel.Equals("stacks", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsClientLegacyMirrorPath(rel))
        {
            return true;
        }

        if (IsArmoryLegacyDataMirrorPath(rel))
        {
            return true;
        }

        if (rel.StartsWith("launcher-build", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith("armory-build", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith("launcher-dist", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsClientLegacyMirrorPath(string rel)
        => rel.Equals("client", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith("client/", StringComparison.OrdinalIgnoreCase);

    private async Task<(bool Allowed, List<string> Blockers)> EvaluateClientMirrorDeletionAsync(
        string normalizedRelative,
        CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        var stacks = await _dbContext.ManagedStacks.AsNoTracking().Where(s => s.ClientEnabled).ToListAsync(cancellationToken);
        if (stacks.Count == 0)
        {
            return (true, blockers);
        }

        IEnumerable<ManagedStackEntity> affected = stacks;
        if (TryParseClientMirrorStackId(normalizedRelative, out var stackId))
        {
            affected = stacks.Where(s => s.Id.Equals(stackId, StringComparison.OrdinalIgnoreCase));
            if (!affected.Any())
            {
                return (true, blockers);
            }
        }

        foreach (var stack in affected)
        {
            var volumeName = DockerComposeOverrideGenerator.ClientBaseVolumeName(stack.Id);
            var summary = await _remoteEngine.GetVolumeTreeSummaryAsync(stack, volumeName, cancellationToken);
            if (ClientBaseVolumeLooksPopulated(summary))
            {
                continue;
            }

            blockers.Add(
                $"{stack.StackName}: acore-{stack.Id}-client-base is missing Wow.exe/Data MPQs ({summary.FileCount} files, {FormatBytesShort(summary.TotalBytes)})");
        }

        return (blockers.Count == 0, blockers);
    }

    private static bool TryParseClientMirrorStackId(string normalizedRelative, out string stackId)
    {
        stackId = string.Empty;
        var rel = normalizedRelative.Replace('\\', '/').Trim('/');
        const string prefix = "client/stacks/";
        if (!rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = rel[prefix.Length..];
        var slash = rest.IndexOf('/');
        stackId = slash >= 0 ? rest[..slash] : rest;
        return !string.IsNullOrWhiteSpace(stackId);
    }

    private static bool ClientBaseVolumeLooksPopulated(VolumeTreeSummary summary)
        => summary.HasWowExe || summary.HasDataMpq;

    private static bool LooksLikeWoWClientRoot(string dir)
        => Directory.EnumerateFiles(dir, "Wow.exe").Any()
            || Directory.EnumerateFiles(dir, "WoW.exe").Any()
            || (Directory.Exists(Path.Combine(dir, "Data"))
                && Directory.EnumerateFiles(Path.Combine(dir, "Data"), "*.MPQ").Any());

    /// <summary>
    /// Legacy armory 3D dataset mirror paths. Styling/config under static/ (outside data/) stays protected.
    /// </summary>
    private static bool IsArmoryLegacyDataMirrorPath(string rel)
    {
        if (!rel.StartsWith("armory-assets/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // armory-assets/stacks/{id}/static/data/...
        const string prefix = "armory-assets/stacks/";
        if (!rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = rel[prefix.Length..];
        var staticDataIdx = rest.IndexOf("/static/data", StringComparison.OrdinalIgnoreCase);
        return staticDataIdx >= 0;
    }

    private static string? DescribeManagerEntry(string relativePath, bool isDirectory)
    {
        if (relativePath.StartsWith("client/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Equals("client", StringComparison.OrdinalIgnoreCase))
        {
            return isDirectory
                ? "Legacy client upload mirror on the manager. Only removable when that stack's client-base Docker volume is verified."
                : "Legacy client mirror file.";
        }

        if (IsArmoryLegacyDataMirrorPath(relativePath.Replace('\\', '/').Trim('/')))
        {
            return "Legacy armory 3D data mirror (safe to remove when the armory-assets volume has the dataset).";
        }

        if (relativePath.StartsWith("armory-assets/", StringComparison.OrdinalIgnoreCase))
        {
            return "Armory styling/config on manager (still used for image rebuilds until fully volume-based).";
        }

        if (relativePath.StartsWith("stacks/", StringComparison.OrdinalIgnoreCase))
        {
            return "AzerothCore build checkout — protected.";
        }

        if (relativePath.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
        {
            return "Platform signing/encryption key — protected.";
        }

        return null;
    }

    private static bool IsManagerVolumeName(string volumeName) =>
        volumeName.StartsWith("azeroth-platform-", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnonymousVolumeName(string volumeName) =>
        volumeName.Length == 64 && Regex.IsMatch(volumeName, @"^[a-f0-9]+$", RegexOptions.IgnoreCase);

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

            var sizeBytes = await ResolveImageSizeBytesAsync(contextArg, row, cancellationToken);
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

            var sizeBytes = await ResolveImageSizeBytesAsync(contextArg, row, cancellationToken);
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

    private static DockerReclaimableBreakdownDto BuildReclaimableBreakdown(
        DockerDiskUsageDto diskUsage,
        List<StackDockerImageDto> danglingImages,
        List<StackDockerImageDto> allPlatformImages,
        List<DockerObsoleteBuildDirDto> obsoleteBuildDirs)
    {
        var deletableUnusedImages = allPlatformImages.Where(i => !i.IsActive).ToList();
        var listedReclaimableBytes = diskUsage.DockerBuildCacheReclaimableBytes
            + danglingImages.Sum(i => i.SizeBytes)
            + deletableUnusedImages.Sum(i => i.SizeBytes)
            + obsoleteBuildDirs.Sum(d => d.SizeBytes);

        return new DockerReclaimableBreakdownDto
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
        };
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
        var existingVolumes = await ListExistingVolumeNamesAsync(contextArg, cancellationToken);

        foreach (var name in names)
        {
            if (existingVolumes is not null)
            {
                if (!existingVolumes.Contains(name))
                {
                    continue;
                }
            }
            else
            {
                var (existsExit, _, _) = await RunDockerAsync($"{contextArg}volume inspect {name}", cancellationToken);
                if (existsExit != 0)
                {
                    continue;
                }
            }

            volumeUsage.TryGetValue(name, out var usage);
            var linkCount = usage.Links;
            if (linkCount <= 0 && existingVolumes is null)
            {
                var (containerExit, containerOutput, _) = await RunDockerAsync(
                    $"{contextArg}ps -a --filter volume={name} -q",
                    cancellationToken);
                linkCount = containerOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            }

            volumes.Add(new StackDockerVolumeDto
            {
                Name = name,
                SizeBytes = usage.SizeBytes,
                LinkCount = linkCount,
                IsActive = true,
                ActiveReason = linkCount > 0
                    ? "Mounted by one or more containers."
                    : "Data volume for a managed stack.",
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

    private async Task<(int RemovedImages, long FreedBytes)> CleanupOldBuildsOnEngineAsync(
        string contextArg,
        HashSet<string> managedStackIds,
        bool anyClientEnabled,
        CancellationToken cancellationToken)
    {
        var removedImages = 0;
        var freedBytes = 0L;
        var allContainerImageRefs = await GetAllContainerImageRefsAsync(contextArg, cancellationToken);

        var (danglingExit, pruneOutput, danglingErr) = await RunDockerAsync($"{contextArg}image prune -f", cancellationToken);
        if (danglingExit != 0)
        {
            _logger.LogWarning(
                "docker image prune failed{Context}: {Err}",
                DescribeDockerContextArg(contextArg),
                danglingErr);
        }
        else
        {
            freedBytes += ParseDockerReclaimedSpace(pruneOutput);
        }

        foreach (var image in await ListDanglingImagesAsync(contextArg, cancellationToken))
        {
            var (exitCode, _, stderr) = await RunDockerAsync($"{contextArg}rmi -f {image.Id}", cancellationToken);
            if (exitCode != 0)
            {
                _logger.LogWarning(
                    "Failed to remove dangling image {Image}{Context}: {Err}",
                    image.Id,
                    DescribeDockerContextArg(contextArg),
                    stderr);
                continue;
            }

            removedImages++;
            freedBytes += image.SizeBytes;
            _logger.LogInformation(
                "Removed dangling docker image {Image}{Context}",
                image.Id,
                DescribeDockerContextArg(contextArg));
        }

        var allPlatformImages = await ListAllPlatformImagesAsync(
            contextArg,
            allContainerImageRefs,
            managedStackIds,
            anyClientEnabled,
            cancellationToken);
        foreach (var image in allPlatformImages.Where(i => !i.IsActive))
        {
            var (exitCode, _, stderr) = await RunDockerAsync($"{contextArg}rmi -f {image.Id}", cancellationToken);
            if (exitCode != 0)
            {
                _logger.LogWarning(
                    "Failed to remove image {Image}{Context}: {Err}",
                    image.Reference,
                    DescribeDockerContextArg(contextArg),
                    stderr);
                continue;
            }

            removedImages++;
            freedBytes += image.SizeBytes;
        }

        return (removedImages, freedBytes);
    }

    private async Task<List<string>> GetDistinctDockerContextArgsAsync(CancellationToken cancellationToken)
    {
        var contextArgs = new List<string> { string.Empty };
        var seen = new HashSet<string>(StringComparer.Ordinal) { string.Empty };

        var externalStacks = await _dbContext.ManagedStacks
            .AsNoTracking()
            .Where(s => s.DeploymentTarget == DeploymentTarget.External)
            .ToListAsync(cancellationToken);

        foreach (var stack in externalStacks)
        {
            var dockerContext = await ResolveDockerContextAsync(stack, cancellationToken);
            var contextArg = ContextArg(dockerContext);
            if (seen.Add(contextArg))
            {
                contextArgs.Add(contextArg);
            }
        }

        return contextArgs;
    }

    private static string DescribeDockerContextArg(string contextArg) =>
        string.IsNullOrWhiteSpace(contextArg) ? string.Empty : " on remote engine";

    private static StackDockerImageDto? FindStackDockerImage(IEnumerable<StackDockerImageDto> sources, string imageId)
    {
        if (string.IsNullOrWhiteSpace(imageId))
        {
            return null;
        }

        var normalized = imageId.Trim();
        foreach (var image in sources)
        {
            if (ImageIdsMatch(image.Id, normalized)
                || string.Equals(image.Reference, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return image;
            }
        }

        return null;
    }

    private static bool ImageIdsMatch(string candidateId, string requestedId)
    {
        if (string.Equals(candidateId, requestedId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var candidate = NormalizeImageId(candidateId);
        var requested = NormalizeImageId(requestedId);
        if (candidate.Length == 0 || requested.Length == 0)
        {
            return false;
        }

        return candidate.StartsWith(requested, StringComparison.OrdinalIgnoreCase)
            || requested.StartsWith(candidate, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeImageId(string imageId)
    {
        var trimmed = imageId.Trim();
        return trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"sha256:{trimmed}";
    }

    private static string ShortImageId(string imageId)
    {
        if (imageId.Length <= 19)
        {
            return imageId;
        }

        return imageId[..19];
    }

    private static long ParseDockerReclaimedSpace(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return 0;
        }

        var match = Regex.Match(
            output,
            @"(?:Total reclaimed space|Total):\s*([\d.,]+)\s*([KMGT]?B)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return 0;
        }

        return ParseHumanSize($"{match.Groups[1].Value}{match.Groups[2].Value}");
    }

    private async Task<string?> ResolveDockerContextAsync(ManagedStackEntity stack, CancellationToken cancellationToken) =>
        stack.DeploymentTarget != DeploymentTarget.External
            ? null
            : await _remoteEngine.EnsureContextAsync(stack, cancellationToken);

    private static string ContextArg(string? dockerContext) =>
        string.IsNullOrWhiteSpace(dockerContext) ? string.Empty : $"--context {dockerContext} ";

    private async Task<HashSet<string>?> ListExistingVolumeNamesAsync(
        string contextArg,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contextArg))
        {
            return null;
        }

        var (exitCode, output, _) = await RunDockerAsync($"{contextArg}volume ls -q", cancellationToken);
        if (exitCode != 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<DockerDiskUsageDto> GetRemoteEngineDiskUsageAsync(
        string contextArg,
        CancellationToken cancellationToken)
    {
        var usage = new DockerDiskUsageDto();
        var (dfExit, dfOutput, _) = await RunDockerAsync(
            $"{contextArg}run --rm alpine:3.20 df -B1 --output=size,used,avail,pcent /",
            cancellationToken);
        if (dfExit == 0)
        {
            ParseHostDisk(dfOutput, usage);
        }

        var (sysExit, sysOutput, _) = await RunDockerAsync($"{contextArg}system df", cancellationToken);
        if (sysExit == 0)
        {
            ParseDockerSystemDf(sysOutput, usage);
        }

        usage.IsWarning = usage.UsedPercent >= DiskWarningThresholdPercent;
        return usage;
    }

    private async Task<long> ResolveImageSizeBytesAsync(
        string contextArg,
        DockerImageRow row,
        CancellationToken cancellationToken)
    {
        var fromListing = ParseHumanSize(row.Size);
        if (!string.IsNullOrWhiteSpace(contextArg))
        {
            // Remote engines: one `docker image inspect` per image over SSH adds minutes of latency.
            return fromListing;
        }

        return await GetImageSizeBytesAsync(contextArg, row.ID, cancellationToken) ?? fromListing;
    }

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

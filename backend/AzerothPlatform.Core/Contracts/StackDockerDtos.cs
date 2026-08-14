namespace AzerothPlatform.Core.Contracts;

/// <summary>Host / Docker VM disk usage for the admin UI.</summary>
public sealed class DockerDiskUsageDto
{
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long AvailableBytes { get; set; }
    public double UsedPercent { get; set; }
    public bool IsWarning { get; set; }
    public long DockerImagesBytes { get; set; }
    public long DockerBuildCacheBytes { get; set; }
    public long ReclaimableBytes { get; set; }
    public long DockerImagesReclaimableBytes { get; set; }
    public long DockerBuildCacheReclaimableBytes { get; set; }
    public long DockerVolumesBytes { get; set; }
    public long DockerContainersBytes { get; set; }
    public long DockerVolumesReclaimableBytes { get; set; }
    public long DockerContainersReclaimableBytes { get; set; }
}

/// <summary>Where Docker / on-disk space is consumed on the engine host.</summary>
public sealed class DockerDiskUsageBreakdownDto
{
    public long DockerImagesBytes { get; set; }
    public int DockerImagesCount { get; set; }
    public long ActiveImagesBytes { get; set; }
    public int ActiveImagesCount { get; set; }
    public long ReclaimableImagesBytes { get; set; }
    public int ReclaimableImagesCount { get; set; }
    public long DockerVolumesBytes { get; set; }
    public int DockerVolumesCount { get; set; }
    public long ActiveVolumesBytes { get; set; }
    public int ActiveVolumesCount { get; set; }
    public long DockerBuildCacheBytes { get; set; }
    public long DockerContainersBytes { get; set; }
    public long ManagedBuildCheckoutBytes { get; set; }
    public int ManagedBuildCheckoutCount { get; set; }
    public long OrphanedBuildCheckoutBytes { get; set; }
    public int OrphanedBuildCheckoutCount { get; set; }
    public long DanglingLayerBytes { get; set; }
    public int DanglingLayerCount { get; set; }
    public long ReclaimableBytes { get; set; }
    public List<StackDockerImageDto> ActiveImages { get; set; } = [];
    public List<StackDockerVolumeDto> ActiveVolumes { get; set; } = [];
}

/// <summary>On-disk build checkout that no longer belongs to a managed stack.</summary>
public sealed class DockerObsoleteBuildDirDto
{
    public string StackId { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

/// <summary>Where reclaimable Docker disk space comes from.</summary>
public sealed class DockerReclaimableBreakdownDto
{
    public long BuildCacheBytes { get; set; }
    public long DanglingImageBytes { get; set; }
    public int DanglingImageCount { get; set; }
    public long UnusedTaggedImageBytes { get; set; }
    public int UnusedTaggedImageCount { get; set; }
    public long ObsoleteBuildDirBytes { get; set; }
    public int ObsoleteBuildDirCount { get; set; }
    public long EngineReclaimableBytes { get; set; }
    public long ListedReclaimableBytes { get; set; }
}

/// <summary>Per-stack Docker disk usage overview for the admin Docker tab.</summary>
public sealed class StackDockerOverviewDto
{
    /// <summary>True when this overview was loaded from a remote Docker engine (VPC) over SSH.</summary>
    public bool IsRemoteEngine { get; set; }

    /// <summary>
    /// True when expensive daemon-wide stats were omitted for speed (dangling layers, unused images, system df).
    /// </summary>
    public bool RemoteStatsLimited { get; set; }

    public DockerDiskUsageDto? DiskUsage { get; set; }
    public DockerDiskUsageBreakdownDto? DiskUsageBreakdown { get; set; }
    public DockerReclaimableBreakdownDto? ReclaimableBreakdown { get; set; }
    public StackDockerBuildFilesDto? BuildFiles { get; set; }
    public List<StackDockerImageDto> Images { get; set; } = [];
    public List<StackDockerImageDto> UnusedImages { get; set; } = [];
    public List<StackDockerImageDto> DanglingImages { get; set; } = [];
    public List<DockerObsoleteBuildDirDto> ObsoleteBuildDirs { get; set; } = [];
    public List<StackDockerVolumeDto> Volumes { get; set; } = [];
    public long BuildCacheBytes { get; set; }
    public long ReclaimableBytes { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class StackDockerBuildFilesDto
{
    public bool Exists { get; set; }
    public string Path { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool IsActive { get; set; }
    public string? ActiveReason { get; set; }
}

public sealed class StackDockerImageDto
{
    public string Id { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string? OwnerStackId { get; set; }
    public long SizeBytes { get; set; }
    public DateTime? CreatedAt { get; set; }
    public bool IsActive { get; set; }
    public string? ActiveReason { get; set; }
}

public sealed class StackDockerVolumeDto
{
    public string Name { get; set; } = string.Empty;
    public long? SizeBytes { get; set; }
    public int LinkCount { get; set; }
    public bool IsActive { get; set; }
    public string? ActiveReason { get; set; }
}

public sealed class StackDockerDeleteResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long FreedBytes { get; set; }
}

public sealed class DockerCleanupResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long FreedBytes { get; set; }
    public int RemovedImages { get; set; }
    public int RemovedBuildDirs { get; set; }
}

/// <summary>Read-only audit of Docker volume usage, drift, and safe cleanup candidates.</summary>
public sealed class DockerVolumeAuditDto
{
    public DateTime AuditedAt { get; set; }
    public List<DockerVolumeAuditDuplicateCopyDto> DuplicateCopies { get; set; } = [];
    public List<DockerVolumeAuditOrphanVolumeDto> OrphanVolumes { get; set; } = [];
    public List<DockerVolumeAuditStaleFileDto> StaleOverlayFiles { get; set; } = [];
    public List<DockerVolumeAuditDriftNoteDto> DriftNotes { get; set; } = [];
    public long ReclaimableBytes { get; set; }
    public int ReclaimableItemCount { get; set; }
}

public sealed class DockerVolumeAuditDuplicateCopyDto
{
    public string Label { get; set; } = string.Empty;
    public string ManagerPath { get; set; } = string.Empty;
    public long ManagerBytes { get; set; }
    public string VolumeName { get; set; } = string.Empty;
    public long VolumeBytes { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed class DockerVolumeAuditOrphanVolumeDto
{
    public string VolumeName { get; set; } = string.Empty;
    public string? InferredStackId { get; set; }
    public long? SizeBytes { get; set; }
    public int LinkCount { get; set; }
    public bool IsSafeToDelete { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class DockerVolumeAuditStaleFileDto
{
    public string VolumeName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool IsSafeToDelete { get; set; }
}

public sealed class DockerVolumeAuditDriftNoteDto
{
    public string Category { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public sealed class DockerVolumeCleanupRequestDto
{
    public List<string> OrphanVolumeNames { get; set; } = [];
    public List<string> StaleOverlayPaths { get; set; } = [];
}

public sealed class DockerVolumeCleanupResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long FreedBytes { get; set; }
    public int DeletedVolumes { get; set; }
    public int DeletedFiles { get; set; }
}

/// <summary>Result of listing containers, including whether the Docker engine responded.</summary>
public sealed class DockerListContainersResult
{
    public IReadOnlyList<ContainerStatusDto> Containers { get; init; } = [];
    public bool EngineReachable { get; init; }
    public string? EngineError { get; init; }
}

/// <summary>Global Docker engine overview for the manager admin UI.</summary>
public sealed class DockerEngineOverviewDto
{
    public DockerDiskUsageDto? DiskUsage { get; set; }
    public DockerReclaimableBreakdownDto? ReclaimableBreakdown { get; set; }
    /// <summary>
    /// Space that &quot;Reclaim disk space&quot; can actually free (build cache, dangling layers, unused images,
    /// orphaned checkouts). Excludes Docker volumes — delete those separately on this page.
    /// </summary>
    public long ReclaimableBytes { get; set; }
    public DockerManagerVolumeDto? ManagerVolume { get; set; }
    public List<DockerEngineVolumeGroupDto> VolumeGroups { get; set; } = [];
    public List<DockerEngineImageDto> Images { get; set; } = [];
    public long TotalVolumeBytes { get; set; }
    public long TotalImageBytes { get; set; }
    public int DeletableVolumeCount { get; set; }
    public long DeletableVolumeBytes { get; set; }
}

public sealed class DockerManagerVolumeDto
{
    public string Name { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public bool IsProtected { get; set; } = true;
    public string Detail { get; set; } = string.Empty;
    public List<DockerVolumeDirectoryEntryDto> Directories { get; set; } = [];
}

public sealed class DockerVolumeDirectoryEntryDto
{
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool IsDeletable { get; set; }
    public string? Detail { get; set; }
}

public sealed class DockerEngineVolumeGroupDto
{
    public string Category { get; set; } = string.Empty;
    public string? StackId { get; set; }
    public string? StackName { get; set; }
    public long TotalBytes { get; set; }
    public List<DockerEngineVolumeEntryDto> Volumes { get; set; } = [];
}

public sealed class DockerEngineVolumeEntryDto
{
    public string Name { get; set; } = string.Empty;
    public long? SizeBytes { get; set; }
    public int LinkCount { get; set; }
    public bool IsProtected { get; set; }
    public bool IsDeletable { get; set; }
    public string? Detail { get; set; }
}

public sealed class DockerEngineImageDto
{
    public string Id { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? OwnerStackId { get; set; }
    public int ContainerCount { get; set; }
    public bool IsProtected { get; set; }
    public bool IsDeletable { get; set; }
}

/// <summary>Phase of the global Docker disk-reclaim background job.</summary>
public enum DockerCleanupJobPhase
{
    Running,
    Completed,
    Failed
}

/// <summary>What the detached Docker cleanup background job is doing.</summary>
public enum DockerCleanupJobAction
{
    ReclaimDiskSpace,
    CleanupOldBuilds
}

/// <summary>
/// Status of the detached Docker disk-reclaim job. Runs in the background so pruning build cache and
/// removing images is not cancelled when the HTTP request ends or the user navigates away.
/// </summary>
public sealed class DockerCleanupJobStatusDto
{
    public string JobId { get; set; } = string.Empty;
    public DockerCleanupJobAction Action { get; set; }
    public DockerCleanupJobPhase Phase { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Error { get; set; }
    public bool? Success { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public long EstimatedReclaimableBytes { get; set; }
    public long FreedBytes { get; set; }
    public int RemovedImages { get; set; }
    public int RemovedBuildDirs { get; set; }

    [System.Text.Json.Serialization.JsonInclude]
    public bool IsRunning => Phase == DockerCleanupJobPhase.Running;
}

public sealed class DockerManagerFileEntryDto
{
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public bool IsDeletable { get; set; }
    public string? Detail { get; set; }
}

public sealed class DockerManagerFilesDto
{
    public string Path { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public List<DockerManagerFileEntryDto> Entries { get; set; } = [];
}

public sealed class DockerPlatformKeyStatusDto
{
    public string Name { get; set; } = string.Empty;
    public bool Present { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed class DockerPlatformKeysDto
{
    public List<DockerPlatformKeyStatusDto> Keys { get; set; } = [];
    public string Detail { get; set; } = string.Empty;
}

public sealed class DockerManagerMirrorCleanupResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long FreedBytes { get; set; }
    public int RemovedPaths { get; set; }
    public List<string> RemovedLabels { get; set; } = [];
}

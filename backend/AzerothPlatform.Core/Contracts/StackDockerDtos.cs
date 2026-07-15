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
    public DockerDiskUsageDto? DiskUsage { get; set; }
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

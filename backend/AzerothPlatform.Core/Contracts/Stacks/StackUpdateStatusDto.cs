namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Update status for a single module
/// </summary>
public class ModuleVersionStatusDto
{
    public string ModuleId { get; set; } = string.Empty;
    
    public string ModuleName { get; set; } = string.Empty;
    
    public bool IsOutdated { get; set; }
    
    public string CurrentCommitSha { get; set; } = string.Empty;
    
    public string LatestCommitSha { get; set; } = string.Empty;
}

/// <summary>
/// Complete update status for a stack
/// </summary>
public class StackUpdateStatusDto
{
    public string StackId { get; set; } = string.Empty;
    
    public bool HasUpdates { get; set; }
    
    public bool IsCoreOutdated { get; set; }
    
    public int OutdatedModuleCount { get; set; }
    
    public string? CurrentCoreSha { get; set; }
    
    public string? LatestCoreSha { get; set; }
    
    public List<ModuleVersionStatusDto> OutdatedModules { get; set; } = new();
    
    public DateTime? LastCheckedAt { get; set; }
    
    /// <summary>
    /// CI/CD build status for the latest available core version (if available)
    /// </summary>
    public CiBuildStatusDto? LatestCoreBuildStatus { get; set; }

    /// <summary>
    /// True when the stack's generated runtime artifacts (.env / docker-compose.override.yml) were
    /// produced by an older manager template and should be re-applied to pick up current fixes
    /// (deployment drift). Distinct from <see cref="HasUpdates"/>, which tracks AzerothCore/module code
    /// updates. Re-applying is done by restarting / re-applying the stack, which regenerates the artifacts.
    /// </summary>
    public bool IsRuntimeConfigOutdated { get; set; }

    /// <summary>Runtime-artifact template version the stack was last generated with (0 = pre-tracking).</summary>
    public int RuntimeArtifactVersion { get; set; }

    /// <summary>Runtime-artifact template version the current manager generates.</summary>
    public int RequiredRuntimeArtifactVersion { get; set; }
}

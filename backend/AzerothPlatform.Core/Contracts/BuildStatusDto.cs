namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Real-time build status information
/// </summary>
public class BuildStatusDto
{
    /// <summary>
    /// Unique identifier for this build
    /// </summary>
    public string BuildId { get; set; } = string.Empty;
    
    /// <summary>
    /// Current phase of the build process
    /// </summary>
    public BuildPhase CurrentPhase { get; set; }
    
    /// <summary>
    /// Overall progress percentage (0-100)
    /// </summary>
    public int ProgressPercent { get; set; }
    
    /// <summary>
    /// Description of current step being executed
    /// </summary>
    public string CurrentStep { get; set; } = string.Empty;
    
    /// <summary>
    /// Most recent log lines (last 50)
    /// </summary>
    public List<string> RecentLogs { get; set; } = new();
    
    /// <summary>
    /// Timestamp when build started
    /// </summary>
    public DateTime StartedAt { get; set; }
    
    /// <summary>
    /// Estimated completion time (null if cannot be estimated)
    /// </summary>
    public DateTime? EstimatedCompletion { get; set; }

    /// <summary>
    /// Error message if the build failed (null if build succeeded or is in progress)
    /// </summary>
    public string? ErrorMessage { get; set; }
}

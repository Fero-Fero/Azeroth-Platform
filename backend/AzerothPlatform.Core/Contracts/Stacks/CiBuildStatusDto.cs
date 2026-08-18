namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Represents the CI/CD build status for a commit
/// </summary>
public class CiBuildStatusDto
{
    /// <summary>
    /// Overall status: "success", "failure", "pending", "unknown"
    /// </summary>
    public string Status { get; set; } = "unknown";
    
    /// <summary>
    /// Individual check results for critical workflows
    /// </summary>
    public List<CiCheckDto> CriticalChecks { get; set; } = new();
    
    /// <summary>
    /// When the CI status was last checked
    /// </summary>
    public DateTime CheckedAt { get; set; }
    
    /// <summary>
    /// Total number of checks that ran
    /// </summary>
    public int TotalChecks { get; set; }
    
    /// <summary>
    /// Number of checks that passed
    /// </summary>
    public int PassedChecks { get; set; }
    
    /// <summary>
    /// Number of checks that failed
    /// </summary>
    public int FailedChecks { get; set; }
}

/// <summary>
/// Represents an individual CI check/workflow run
/// </summary>
public class CiCheckDto
{
    /// <summary>
    /// Name of the check/workflow (e.g., "build-containers")
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Status: "completed", "in_progress", "queued"
    /// </summary>
    public string Status { get; set; } = string.Empty;
    
    /// <summary>
    /// Conclusion: "success", "failure", "neutral", "cancelled", "skipped", "timed_out", "action_required"
    /// </summary>
    public string? Conclusion { get; set; }
    
    /// <summary>
    /// Link to the check run on GitHub
    /// </summary>
    public string? HtmlUrl { get; set; }
}

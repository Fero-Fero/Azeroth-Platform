namespace AzerothPlatform.Infrastructure.Configuration;

/// <summary>
/// Configuration options for GitHub API integration
/// </summary>
public sealed class GitHubOptions
{
    /// <summary>
    /// List of critical workflow names to check for build status
    /// </summary>
    public List<string> CriticalWorkflows { get; set; } = new()
    {
        "build-containers",
        "Build and Integration Test"
    };
}

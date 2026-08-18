using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Service for interacting with GitHub API
/// </summary>
public interface IGitHubApiService
{
    /// <summary>
    /// Gets the CI/CD build status for a specific commit SHA
    /// </summary>
    /// <param name="repository">Repository in format "owner/repo" (e.g., "azerothcore/azerothcore-wotlk")</param>
    /// <param name="commitSha">Commit SHA to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>CI build status with check results</returns>
    Task<CiBuildStatusDto> GetCommitBuildStatusAsync(
        string repository, 
        string commitSha, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the latest commit SHA for a branch
    /// </summary>
    /// <param name="repository">Repository in format "owner/repo"</param>
    /// <param name="branch">Branch name (e.g., "master")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Commit SHA</returns>
    Task<string?> GetLatestCommitShaAsync(
        string repository, 
        string branch, 
        CancellationToken cancellationToken = default);
}

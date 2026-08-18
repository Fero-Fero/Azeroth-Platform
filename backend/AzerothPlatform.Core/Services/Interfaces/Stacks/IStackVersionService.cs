using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Service for checking stack version status against remote repositories
/// </summary>
public interface IStackVersionService
{
    /// <summary>
    /// Check update status for a stack and cache results in database.
    /// Performs git operations to compare local vs remote commits.
    /// </summary>
    Task<StackUpdateStatusDto> CheckAndCacheStatusAsync(string stackId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Get cached update status from database (fast, no git operations).
    /// Returns null if stack doesn't exist or has never been checked.
    /// </summary>
    Task<StackUpdateStatusDto?> GetCachedStatusAsync(string stackId, CancellationToken cancellationToken = default);
}

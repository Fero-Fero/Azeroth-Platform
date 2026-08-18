using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Service for discovering existing AzerothCore stacks from filesystem and Docker
/// </summary>
public interface IStackDiscoveryService
{
    /// <summary>
    /// Discover all stacks that exist in the filesystem/Docker but are not tracked in the database
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of discovered stacks that can be imported</returns>
    Task<IReadOnlyList<DiscoveredStackDto>> DiscoverStacksAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Discover a single stack by ID
    /// </summary>
    /// <param name="stackId">Stack identifier</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Discovered stack information, or null if not found</returns>
    Task<DiscoveredStackDto?> DiscoverStackByIdAsync(string stackId, CancellationToken ct = default);
}

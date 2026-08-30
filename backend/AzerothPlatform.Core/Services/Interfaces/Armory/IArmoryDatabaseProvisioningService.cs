namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Provisions a least-privilege MySQL user for the per-stack armory container.
/// </summary>
public interface IArmoryDatabaseProvisioningService
{
    /// <summary>MySQL username injected into the armory compose environment.</summary>
    string Username { get; }

    /// <summary>
    /// Ensures a random armory DB password exists on the stack entity (encrypted at rest).
    /// Does not require the game database to be running.
    /// </summary>
    Task EnsurePasswordAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates armory extension tables and (re)applies grants on the stack's MySQL instance.
    /// Requires the database container to be reachable. Best-effort on failure.
    /// </summary>
    Task EnsureProvisionedAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Returns credentials for compose rendering for the given stack.</summary>
    Task<(string User, string Password)> GetCredentialsAsync(string stackId, CancellationToken cancellationToken = default);
}

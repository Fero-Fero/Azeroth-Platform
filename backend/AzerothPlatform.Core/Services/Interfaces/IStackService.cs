using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Persists and retrieves managed AzerothCore stacks.
/// </summary>
public interface IStackService
{
    Task<IReadOnlyList<StackDetailsDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<StackDetailsDto?> GetAsync(string stackId, CancellationToken cancellationToken = default);

    Task<StackDetailsDto> CreateAsync(StackConfigurationDto configuration, CancellationToken cancellationToken = default);

    Task<StackDetailsDto?> UpdateAsync(string stackId, StackConfigurationDto configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-establishes SSH/docker context for an external stack after platform keys were lost. Requires a
    /// fresh SSH private key and validates the connection before saving.
    /// </summary>
    Task<StackDetailsDto?> ReconnectExternalAsync(
        string stackId,
        DeploymentConfigDto deployment,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the player-facing host/IP for the whole stack: persisted realmlist override, live
    /// acore_auth.realmlist rows, regenerated runtime artifacts, launcher registry/client data, and
    /// running player-facing services that need recreated to pick up new environment/bind settings.
    /// </summary>
    Task<bool> ApplyStackPublicHostAsync(string stackId, string host, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string stackId, CancellationToken cancellationToken = default);

    Task<bool> StartAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the action the build pipeline should run after the next successful build (used by the
    /// Update action to request a pre-build snapshot + post-build SQL reapply + reboot).
    /// </summary>
    Task<bool> SetPostBuildActionAsync(string stackId, PostBuildAction action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records how the next build should reconcile the operator's existing server .conf files with the
    /// freshly regenerated configs (used by the Update/Rebuild actions). Cleared after the build applies it.
    /// </summary>
    Task<bool> SetConfigMigrationModeAsync(string stackId, ConfigMigrationMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts only the stack's database service (ac-database), leaving the world/auth servers
    /// stopped. Useful for applying SQL/patches or DB maintenance without a full stack start; the
    /// stack is reported as <see cref="StackStatus.Degraded"/> while only the database is up.
    /// </summary>
    Task<bool> StartDatabaseAsync(string stackId, CancellationToken cancellationToken = default);

    Task<bool> StopAsync(string stackId, CancellationToken cancellationToken = default);

    Task<bool> RestartAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds (if needed) and starts the per-stack armory (frontend-armory) container. The rest of
    /// the stack is untouched. Throws if the armory image cannot be built.
    /// </summary>
    Task<bool> StartArmoryAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops and removes the per-stack armory container, leaving the rest of the stack running.
    /// </summary>
    Task<bool> StopArmoryAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a lifecycle action (start/stop/restart/recreate) to a single compose service of the
    /// stack (database, auth/world servers, armory, or an init/utility service). Returns false when
    /// the stack does not exist; throws for an unknown/unmanaged service or a disallowed state.
    /// </summary>
    Task<bool> ServiceActionAsync(string stackId, string service, StackServiceAction action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Regenerates runtime configuration and force-recreates the worldserver and authserver so
    /// changed .conf files and newly-added Lua scripts are picked up. Used by the config/lua editors.
    /// </summary>
    Task<bool> RestartServerProcessesAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the stack's current player-facing HTTP network settings (armory/client host ports and the
    /// publish bind interface, plus the effective bind after policy is applied). Null when the stack does
    /// not exist.
    /// </summary>
    Task<ArmoryNetworkConfigDto?> GetArmoryNetworkAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the stack's armory/client host ports and publish bind interface, regenerates the runtime
    /// config, and (if the stack is running) force-recreates the armory + client containers so the new
    /// binding takes effect immediately. Returns the updated settings, or null when the stack does not
    /// exist. Throws <see cref="ArgumentException"/> for an invalid bind address or a port that is out of
    /// range or already in use.
    /// </summary>
    Task<ArmoryNetworkConfigDto?> UpdateArmoryNetworkAsync(
        string stackId, ArmoryNetworkConfigDto config, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Import a discovered stack into the manager database
    /// </summary>
    /// <param name="stackId">Stack identifier from discovery</param>
    /// <param name="request">Import configuration (name, passwords)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Imported stack details</returns>
    /// <exception cref="StackNotFoundException">Stack not found or orphaned</exception>
    /// <exception cref="StackConflictException">Stack ID or ports conflict with existing stacks</exception>
    Task<StackDetailsDto> ImportDiscoveredStackAsync(
        string stackId, 
        ImportStackRequestDto request, 
        CancellationToken cancellationToken = default);
    
    Task<bool> ApplyModuleConfigAsync(string stackId, Dictionary<string, string> envVars, CancellationToken cancellationToken = default);

    /// <summary>
    /// Initialize the SOAP admin account for a stack by inserting it directly into the auth database.
    /// </summary>
    /// <param name="stackId">Stack identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Credentials if account was freshly created, null if already initialized</returns>
    /// <exception cref="StackNotFoundException">Stack not found</exception>
    /// <exception cref="InvalidOperationException">Stack is not running or database not accessible</exception>
    Task<SoapCredentialsDto?> InitializeAdminAccountAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieve stored SOAP admin credentials for a stack (for recovery purposes).
    /// </summary>
    /// <returns>Credentials or null if the stack does not exist</returns>
    Task<SoapCredentialsDto?> GetSoapCredentialsAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieve the stored MySQL root credentials for a stack. This is a sensitive reveal operation
    /// (audited by the implementation) and is intentionally separate from the standard detail payload,
    /// which no longer includes secrets.
    /// </summary>
    /// <returns>Credentials or null if the stack does not exist</returns>
    Task<DatabaseCredentialsDto?> GetDatabaseCredentialsAsync(string stackId, CancellationToken cancellationToken = default);
}

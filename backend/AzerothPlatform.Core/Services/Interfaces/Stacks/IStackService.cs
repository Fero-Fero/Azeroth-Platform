using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Persists and retrieves managed AzerothCore stacks.
/// </summary>
public interface IStackService
{
    Task<IReadOnlyList<StackDetailsDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes live Docker status for every stack (once per list refresh) and caches the result for
    /// <see cref="ListAsync"/> until the next probe or a stack detail refresh.
    /// </summary>
    Task<IReadOnlyList<StackDetailsDto>> ProbeAllStacksForListAsync(CancellationToken cancellationToken = default);

    Task<StackDetailsDto?> GetAsync(string stackId, CancellationToken cancellationToken = default);

    Task<StackDetailsDto> CreateAsync(StackConfigurationDto configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a <see cref="StackStatus.SetupIncomplete"/> stack after a cloud VPC exists
    /// so My stacks can resume the wizard later.
    /// </summary>
    Task<StackDetailsDto> SaveSetupDraftAsync(StackSetupDraftRequestDto request, CancellationToken cancellationToken = default);

    /// <summary>Returns wizard snapshot and SSH key for an incomplete stack. Null when not a draft.</summary>
    Task<StackSetupDraftDto?> GetSetupDraftAsync(string stackId, CancellationToken cancellationToken = default);

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
    /// Persists the player-facing host and enqueues a background job to apply it across live services.
    /// When the stack is stopped the job temporarily starts the database (and client if enabled),
    /// updates the realmlist, refreshes the launcher registry, then restores the previous state.
    /// </summary>
    Task<SetRealmAddressResponseDto> BeginApplyStackPublicHostAsync(
        string stackId,
        string host,
        CancellationToken cancellationToken = default);

    /// <summary>Live apply steps for a host already persisted on the stack entity.</summary>
    Task ApplyStackPublicHostLiveAsync(
        string stackId,
        Action<PublicHostApplyStepDto>? reportStep,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string stackId,
        bool terminateCloudInstance = false,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Aggressively tears down all stack containers, including crash-looping services. Used when a
    /// normal stop is blocked by an in-flight lifecycle job or <c>restart: unless-stopped</c> loops.
    /// </summary>
    Task<bool> ForceStopAsync(string stackId, CancellationToken cancellationToken = default);

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
    /// Builds (if needed) and starts the per-stack client file-server container. The rest of the stack
    /// is untouched.
    /// </summary>
    Task<bool> StartClientAsync(string stackId, bool forceRecreate = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops and removes the per-stack client file-server container, leaving the rest of the stack running.
    /// </summary>
    Task<bool> StopClientAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restarts the per-stack client file-server container after ensuring it is present in the compose override.
    /// </summary>
    Task<bool> RestartClientAsync(string stackId, CancellationToken cancellationToken = default);

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

    /// <summary>Syncs Linux host firewall (ufw) rules for an external stack's player/web ports.</summary>
    Task<RemoteSetupResultDto?> SyncVpcFirewallAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Starts or installs Docker on an external stack's VPC over SSH.</summary>
    Task<RemoteSetupResultDto?> ProvisionVpcDockerAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks root and image-default users out of internet SSH on an external stack. Operator user stays.
    /// </summary>
    Task<RemoteSetupResultDto?> FinalizeSshHardeningAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Suggested host/cloud firewall rules for an external stack.</summary>
    Task<VpcSecurityProfileDto?> GetVpcSecurityProfileAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Live host firewall and Docker bind verification for an external stack.</summary>
    Task<VpcFirewallStatusDto?> GetVpcFirewallStatusAsync(string stackId, CancellationToken cancellationToken = default);

    /// <summary>Recent SSH login and failed-attempt events from the remote VPC host.</summary>
    Task<VpcSshLogsDto?> GetVpcSshLogsAsync(string stackId, int limit = 100, CancellationToken cancellationToken = default);
    
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

namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Advanced configuration options for AzerothCore server
/// </summary>
public class AdvancedConfigDto
{
    /// <summary>
    /// Maximum number of concurrent players (default: 100)
    /// </summary>
    public int MaxPlayers { get; set; } = 100;
    
    /// <summary>
    /// Display name for the realm
    /// </summary>
    public string RealmName { get; set; } = string.Empty;

    /// <summary>
    /// Public/LAN address clients use to reach this realm's auth AND world servers. Written into the
    /// launcher's realmlist.wtf and into the acore_auth.realmlist DB row. Blank falls back to the
    /// deployment-wide default (Migrations:RealmlistHost). For external stacks this defaults to the
    /// external host.
    /// </summary>
    public string RealmlistHost { get; set; } = string.Empty;

    /// <summary>
    /// Per-service environment variables: <c>serviceId → (envVarName → value)</c>. Environment variables
    /// are per-container, so each service (worldserver, authserver, armory, client) has its own bucket
    /// that is injected into that service's compose <c>environment:</c> block.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ServiceEnvVars { get; set; } = new();
}

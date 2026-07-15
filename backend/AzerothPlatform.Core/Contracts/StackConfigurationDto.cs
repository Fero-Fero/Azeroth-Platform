namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Complete configuration for an AzerothCore stack
/// </summary>
public class StackConfigurationDto
{
    /// <summary>
    /// Unique name for the stack (alphanumeric with dashes)
    /// </summary>
    public string StackName { get; set; } = string.Empty;
    
    /// <summary>
    /// Server type (Standard or Playerbots)
    /// </summary>
    public ServerType ServerType { get; set; }
    
    /// <summary>
    /// List of module IDs to include in build
    /// </summary>
    public List<string> ModuleIds { get; set; } = new();
    
    /// <summary>
    /// Database configuration
    /// </summary>
    public DatabaseConfigDto Database { get; set; } = new();
    
    /// <summary>
    /// Port assignments
    /// </summary>
    public PortConfigDto Ports { get; set; } = new();
    
    /// <summary>
    /// Advanced configuration options
    /// </summary>
    public AdvancedConfigDto Advanced { get; set; } = new();

    /// <summary>
    /// Deployment target (local vs external remote Docker host over SSH).
    /// </summary>
    public DeploymentConfigDto Deployment { get; set; } = new();

    /// <summary>
    /// User-supplied core repository, used only when <see cref="ServerType"/> is a type that allows a
    /// custom repository (e.g. <see cref="ServerType.Custom"/>). Ignored for catalog-defined types.
    /// </summary>
    public CustomForkConfigDto? CustomFork { get; set; }

    /// <summary>Armory player-account options (email confirmation, SMTP when enabled).</summary>
    public ArmoryAccountsConfigDto ArmoryAccounts { get; set; } = new();
}

/// <summary>
/// A user-provided AzerothCore fork to build from when using the custom server type.
/// </summary>
public class CustomForkConfigDto
{
    /// <summary>Git repository URL (http/https) of the fork to clone as the core.</summary>
    public string RepositoryUrl { get; set; } = string.Empty;

    /// <summary>Branch to clone. Defaults to "master" when empty.</summary>
    public string Branch { get; set; } = string.Empty;
}

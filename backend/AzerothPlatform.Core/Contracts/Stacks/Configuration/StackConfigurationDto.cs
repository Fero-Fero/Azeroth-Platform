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
    /// Catalog addon ids to install after the stack client is available (Express wizard).
    /// </summary>
    public List<string> AddonIds { get; set; } = new();

    /// <summary>
    /// Per-stack git branch override keyed by catalog module id. When omitted, the catalog branch is used.
    /// </summary>
    public Dictionary<string, string> ModuleBranches { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    
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

    /// <summary>
    /// When false, the stack never builds or starts the armory.
    /// Omitted on create means include, except Express Setup which omits as exclude.
    /// </summary>
    public bool? IncludeArmory { get; set; }

    /// <summary>
    /// Random playerbot count written into <c>playerbots.conf</c> after first conf seed (0–2500).
    /// Used by Express setup; ignored for other server types unless explicitly applied.
    /// </summary>
    public int RandomBotCount { get; set; }

    /// <summary>
    /// When set, <c>POST /api/stacks</c> completes this <see cref="StackStatus.SetupIncomplete"/> draft
    /// instead of creating a new stack id.
    /// </summary>
    public string? DraftStackId { get; set; }

    /// <summary>True when this stack is configured to include the armory (default yes).</summary>
    public bool IncludesArmory() => ResolveIncludeArmory(ServerType, IncludeArmory);

    /// <summary>
    /// Express Setup omits the armory unless it is explicitly enabled; other types omit as include.
    /// </summary>
    public static bool ResolveIncludeArmory(ServerType serverType, bool? includeArmory) =>
        serverType == ServerType.Express
            ? includeArmory == true
            : includeArmory != false;
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

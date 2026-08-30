using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Configuration.ServerTypes;

namespace AzerothPlatform.Infrastructure.Configuration;

/// <summary>
/// Optional override of the server-type catalog. Built-in entries live in
/// <c>Configuration/ServerTypes/</c>. Binding an empty <c>ServerTypeCatalog</c> section (or omitting
/// it) uses <see cref="Defaults"/>.
/// </summary>
public sealed class ServerTypeCatalogOptions
{
    public const string SectionName = "ServerTypeCatalog";

    public List<ServerTypeDefinition> ServerTypes { get; set; } = new();

    public List<ModuleVisibilityRule> ModuleRules { get; set; } = new();

    public static ServerTypeCatalogOptions Defaults => new()
    {
        ServerTypes =
        [
            Standard.Catalog,
            Playerbots.Catalog,
            IndividualProgression.Catalog,
            NpcBots.Catalog,
            Custom.Catalog,
            Express.Catalog
        ],
        ModuleRules = [.. ModuleVisibilityRules.All]
    };
}

/// <summary>
/// A single server-type definition: which core repository it clones, how it is presented in the
/// wizard, and which modules are bundled into its core (and therefore hidden as installable modules).
/// </summary>
public sealed class ServerTypeDefinition
{
    public ServerType Id { get; set; }

    public bool Enabled { get; set; } = true;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Icon { get; set; } = "server";

    public string CoreRepositoryUrl { get; set; } = string.Empty;

    public string CoreBranch { get; set; } = "master";

    public bool AllowCustomRepository { get; set; }

    public List<string> BundledModuleIds { get; set; } = new();

    public List<string> RequiredModuleIds { get; set; } = new();

    /// <summary>When true, this type is only valid for local deployments.</summary>
    public bool LocalOnly { get; set; }
}

public sealed class ModuleVisibilityRule
{
    public string ModuleId { get; set; } = string.Empty;

    public List<ServerType>? VisibleForServerTypes { get; set; }

    public List<ServerType>? HiddenForServerTypes { get; set; }

    public List<RepositoryOverride> RepositoryOverrides { get; set; } = new();
}

public sealed class RepositoryOverride
{
    public ServerType ServerType { get; set; }

    public string Repository { get; set; } = string.Empty;

    public string Branch { get; set; } = "master";
}

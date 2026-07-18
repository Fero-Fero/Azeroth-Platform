using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Configuration;

/// <summary>
/// Operator-editable catalog that maps each <see cref="ServerType"/> to the core repository it is
/// built from and the module-visibility rules that govern which modules appear in the wizard for that
/// type. Bound from the <c>ServerTypeCatalog</c> configuration section (appsettings / env vars). When
/// the section is missing or empty the built-in <see cref="Defaults"/> are used, so the platform works
/// out of the box; overriding any part in configuration replaces the corresponding default entry.
/// </summary>
public sealed class ServerTypeCatalogOptions
{
    public const string SectionName = "ServerTypeCatalog";

    /// <summary>Per-server-type definitions (core repo, branch, display metadata, bundled modules).</summary>
    public List<ServerTypeDefinition> ServerTypes { get; set; } = new();

    /// <summary>
    /// Per-module visibility rules and repository overrides. Keyed by module id. Modules without a rule
    /// are visible for every server type (except where bundled into the core).
    /// </summary>
    public List<ModuleVisibilityRule> ModuleRules { get; set; } = new();

    /// <summary>
    /// Built-in catalog used when no configuration is supplied. This is the single source of truth for
    /// the default behaviour and is intentionally easy to read/copy into appsettings for customization.
    /// </summary>
    public static ServerTypeCatalogOptions Defaults => new()
    {
        ServerTypes =
        [
            new ServerTypeDefinition
            {
                Id = ServerType.Standard,
                Enabled = true,
                DisplayName = "Standard",
                Description = "Vanilla AzerothCore — the classic WotLK experience. Playerbots can be added as a module.",
                Icon = "server",
                CoreRepositoryUrl = "https://github.com/azerothcore/azerothcore-wotlk.git",
                CoreBranch = "master",
                BundledModuleIds = []
            },
            new ServerTypeDefinition
            {
                Id = ServerType.Playerbots,
                Enabled = true,
                DisplayName = "Playerbots",
                Description = "Official Playerbots fork with the module already integrated so you can level and raid solo.",
                Icon = "bot",
                CoreRepositoryUrl = "https://github.com/mod-playerbots/azerothcore-wotlk.git",
                CoreBranch = "Playerbot",
                // Playerbots is compiled into this fork, so the standalone module is hidden here.
                BundledModuleIds = ["mod-playerbots"]
            },
            new ServerTypeDefinition
            {
                Id = ServerType.IndividualProgression,
                Enabled = true,
                DisplayName = "Individual Progression",
                Description = "Grimfeather fork that simulates progression through expansions and tiers, per character.",
                Icon = "trending-up",
                CoreRepositoryUrl = "https://github.com/Grimfeather/azerothcore-wotlk.git",
                CoreBranch = "master",
                BundledModuleIds = [],
                RequiredModuleIds = ["mod-individual-progression", "mod-playerbots"]
            },
            new ServerTypeDefinition
            {
                Id = ServerType.NpcBots,
                Enabled = true,
                DisplayName = "NPCBots",
                Description = "AzerothCore with NPCBots integrated — hire NPC companions directly in the world.",
                Icon = "users",
                CoreRepositoryUrl = "https://github.com/trickerer/AzerothCore-wotlk-with-NPCBots.git",
                CoreBranch = "npcbots_3.3.5",
                BundledModuleIds = []
            },
            new ServerTypeDefinition
            {
                Id = ServerType.Custom,
                Enabled = true,
                DisplayName = "Custom Fork",
                Description = "Build from your own AzerothCore fork — paste a GitHub repository URL and branch.",
                Icon = "git-fork",
                // Repository/branch are supplied per stack, so none are configured here.
                CoreRepositoryUrl = string.Empty,
                CoreBranch = "master",
                AllowCustomRepository = true,
                BundledModuleIds = []
            }
        ],
        ModuleRules =
        [
            // Playerbots module: installable on Standard, Individual Progression and Custom. It is hidden
            // for Playerbots (bundled into that core) and for NpcBots (the two bot systems cannot coexist).
            new ModuleVisibilityRule
            {
                ModuleId = "mod-playerbots",
                HiddenForServerTypes = [ServerType.Playerbots, ServerType.NpcBots]
            },
            // Individual Progression module: requires the Grimfeather core fork, so it is only shown for
            // the Individual Progression type and cloned from the Grimfeather repository there.
            new ModuleVisibilityRule
            {
                ModuleId = "mod-individual-progression",
                VisibleForServerTypes = [ServerType.IndividualProgression],
                RepositoryOverrides =
                [
                    new RepositoryOverride
                    {
                        ServerType = ServerType.IndividualProgression,
                        Repository = "https://github.com/Grimfeather/mod-individual-progression",
                        Branch = "master"
                    }
                ]
            },
            new ModuleVisibilityRule
            {
                ModuleId = "mod-dungeon-clear",
                HiddenForServerTypes = [ServerType.NpcBots]
            },
            new ModuleVisibilityRule
            {
                ModuleId = "mod-playerbot-dungeon-sim",
                HiddenForServerTypes = [ServerType.NpcBots]
            }
        ]
    };
}

/// <summary>
/// A single server-type definition: which core repository it clones, how it is presented in the
/// wizard, and which modules are bundled into its core (and therefore hidden as installable modules).
/// </summary>
public sealed class ServerTypeDefinition
{
    /// <summary>The <see cref="ServerType"/> enum value this entry configures.</summary>
    public ServerType Id { get; set; }

    /// <summary>When false, the type is hidden from the wizard (existing stacks keep working).</summary>
    public bool Enabled { get; set; } = true;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Icon key resolved to a lucide icon by the frontend (e.g. server, bot, trending-up, users).</summary>
    public string Icon { get; set; } = "server";

    /// <summary>Core git repository (http/https) cloned for this server type.</summary>
    public string CoreRepositoryUrl { get; set; } = string.Empty;

    /// <summary>Core git branch cloned for this server type.</summary>
    public string CoreBranch { get; set; } = "master";

    /// <summary>
    /// When true, the core repository/branch is supplied by the operator at stack-creation time (the
    /// wizard shows a repository URL + branch field) instead of being read from <see cref="CoreRepositoryUrl"/>.
    /// </summary>
    public bool AllowCustomRepository { get; set; }

    /// <summary>
    /// Module ids compiled into this core fork. They are hidden from the module picker for this type
    /// because installing them again would conflict with the bundled copy.
    /// </summary>
    public List<string> BundledModuleIds { get; set; } = new();

    /// <summary>
    /// Module ids that must be selected for stacks of this server type. Unlike bundled modules these
    /// are still installed as separate modules but are auto-selected and locked in the wizard.
    /// </summary>
    public List<string> RequiredModuleIds { get; set; } = new();
}

/// <summary>
/// Visibility and repository rules for a single module id, evaluated against the selected server type.
/// </summary>
public sealed class ModuleVisibilityRule
{
    public string ModuleId { get; set; } = string.Empty;

    /// <summary>
    /// Allowlist: when non-empty, the module is only visible for these server types. Null/empty means
    /// visible for all types (subject to <see cref="HiddenForServerTypes"/> and bundled-module rules).
    /// </summary>
    public List<ServerType>? VisibleForServerTypes { get; set; }

    /// <summary>Blocklist: the module is hidden for these server types.</summary>
    public List<ServerType>? HiddenForServerTypes { get; set; }

    /// <summary>Per-server-type repository/branch overrides used when cloning the module.</summary>
    public List<RepositoryOverride> RepositoryOverrides { get; set; } = new();
}

/// <summary>A repository/branch override applied to a module for a specific server type.</summary>
public sealed class RepositoryOverride
{
    public ServerType ServerType { get; set; }

    public string Repository { get; set; } = string.Empty;

    public string Branch { get; set; } = "master";
}

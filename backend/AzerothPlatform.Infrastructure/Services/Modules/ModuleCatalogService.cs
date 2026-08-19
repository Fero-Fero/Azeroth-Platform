using System.Net;
using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Module catalog combining built-in modules (defined in code) with custom modules persisted in
/// the database and managed through the catalog admin. Custom modules may be cloned from a git
/// repository or supplied as an uploaded package (.zip).
/// </summary>
public sealed partial class ModuleCatalogService : IModuleCatalogService
{
    private static readonly IReadOnlyList<ModuleDto> BuiltInModules =
    [
                new()
        {
            Id = "mod-raid-progression-tracker",
            Name = "Raid Progression Tracker",
            Description = "An AzerothCore module that records per-character boss kills across dungeons, raids and world bosses, for all expansions. Each kill is upserted into a characters-database table with a content_type so progression can be queried per category.",
            Repository = "https://github.com/Fero-Fero/mod-raid-progression-tracker",
            Branch = "main",
            IsBuiltIn = true,
            Recommended = true
        },
        new()
        {
            Id = "mod-raid-logs-tracker",
            Name = "Raid Logs Tracker",
            Description = "An AzerothCore module that times how fast players clear dungeons, raids and world bosses. Instance clears (first enter to final boss) and per-boss kill times are upserted into a characters-database table, driven by an admin-editable instance/boss catalogue in the world database. Powers the armory's Logs and Top Logs pages.",
            Repository = "https://github.com/Fero-Fero/mod-raid-logs-tracker",
            Branch = "main",
            IsBuiltIn = true,
            Recommended = true
        },
        new()
        {
            Id = "mod-ah-bot",
            Name = "Auction House Bot",
            Description = "Adds AI-driven auction house activity to improve economy simulation.",
            Repository = "https://github.com/NathanHandley/mod-ah-bot-plus",
            Branch = "master",
            IsBuiltIn = true,
        },
        new()
        {
            Id = "mod-autobalance",
            Name = "Auto Balance",
            Description = "Automatically scales dungeon and raid difficulty to the active group size.",
            Repository = "https://github.com/azerothcore/mod-autobalance",
            Branch = "master",
            IsBuiltIn = true,
        },
        new()
        {
            Id = "mod-transmog",
            Name = "Transmogrification",
            Description = "Lets players change item appearance while keeping original stats.",
            Repository = "https://github.com/azerothcore/mod-transmog",
            Branch = "master",
            IsBuiltIn = true,
        },
        new()
        {
            Id = "mod-playerbots",
            Name = "Playerbots",
            Description = "Enables AI-controlled party members and world bots. Not available on the Playerbots fork (already bundled) or NpcBots.",
            Repository = "https://github.com/mod-playerbots/mod-playerbots",
            Branch = "master",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-ale",
            Name = "AzerothCore Lua Engine (ALE)",
            Description = "A Lua scripting engine for AzerothCore (an evolved fork of Eluna). Required for the stack's Game \u2192 Lua Scripts tab to work \u2014 without a Lua engine compiled into the worldserver, uploaded Lua scripts won't run. Compiles with the default Lua 5.2.",
            Repository = "https://github.com/azerothcore/mod-ale",
            Branch = "master",
            IsBuiltIn = true,
            Recommended = true
        },
        new()
        {
            Id = "mod-accountbound",
            Name = "Account Bound",
            Description = "This module aims to make mounts, companions and achievements shared across all characters of an account.",
            Repository = "https://github.com/pangolp/mod-accountbound",
            Branch = "master",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-auctionator",
            Name = "Auctionator",
            Description = "This mod is meant to keep a healthy auction house stocked on a low-pop server. It's in it's early phases of building/testing/configuration but keeps a LOT of stuff in the AH.",
            Repository = "https://github.com/kadeshar/mod-auctionator",
            Branch = "main",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-acore-aoe-loot",
            Name = "Azerothcore AoE Loot",
            Description = "This module enables Area of Effect (AOE) looting functionality for AzerothCore, allowing players to loot multiple nearby corpses by interacting with just one of them. All items and gold from corpses within the configured range are automatically collected into a single loot window.",
            Repository = "https://github.com/azerothcore/mod-aoe-loot",
            Branch = "master",
            IsBuiltIn = true,
        },
        new()
        {
            Id = "black-market-auction-house",
            Name = "Black Market Auction House",
            Description = "A faithful backport of the Mists of Pandaria Black Market Auction House to AzerothCore 3.3.5 using the Eluna Lua engine. Extra-data install copies Server Files/lua_scripts onto the stack and Client Files/AddOns into the client. Requires ALE.",
            Repository = "https://github.com/Youpeoples/Black-Market-Auction-House.git",
            Branch = "main",
            IsBuiltIn = true,
            RequiredModuleIds = ["mod-ale"]
        },
        new()
        {
            Id = "mod-gain-honor-guard",
            Name = "Gain Honor Guard",
            Description = "This module gives players the ablilty to farm Guards and/or Elites for Honor.",
            Repository = "https://github.com/azerothcore/mod-gain-honor-guard",
            Branch = "master",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-geddon-binding-shard",
            Name = "Geddon Binding Shard",
            Description = "AzerothCore module for adding configurable custom item drops to boss corpse loot.",
            Repository = "https://github.com/Day36512/mod-geddon-binding-shard",
            Branch = "main",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-individual-progression",
            Name = "Individual Progression",
            Description = "This module simulates progress through expansions and expansion tiers for individual players.",
            Repository = "https://github.com/Grimfeather/mod-individual-progression",
            Branch = "master",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-mount-feeding",
            Name = "Mount Feeding",
            Description = "Mount satisfaction system for AzerothCore 3.3.5a. Mounts must be fed to maintain full speed - similar to the hunter pet happiness mechanic.",
            Repository = "https://github.com/claudevandort/mod-mount-feeding",
            Branch = "master",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-mount-scaling",
            Name = "Mount Scaling",
            Description = "An AzerothCore module that replaces WoW's binary mount speed system with smooth, level-based speed progression. Instead of jumping from 0% to 60% to 100% at fixed level thresholds, mount speed scales gradually as your character levels up.",
            Repository = "https://github.com/claudevandort/mod-mount-scaling",
            Branch = "master",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-multibot-bridge",
            Name = "Multibot Bridge",
            Description = "It provides a structured addon-message bridge between the client UI and the server, allowing MultiBot to refresh bot data without relying on automatic legacy chat parsing.",
            Repository = "https://github.com/Wishmaster117/mod-multibot-bridge.git",
            Branch = "main",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-no-profession-limit",
            Name = "No Profession Limit",
            Description = "NoProfessionLimit is an AzerothCore module for WotLK 3.3.5a that raises or removes the default two-primary-profession limit.",
            Repository = "https://github.com/AlsoNotMehh/NoProfessionLimit.git",
            Branch = "main",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-random-enchants",
            Name = "Random Enchants",
            Description = "Chance to add random enchants to items when looted",
            Repository = "https://github.com/azerothcore/mod-random-enchants",
            Branch = "master",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-new-aoe-loot",
            Name = "Rewritten AoE Loot",
            Description = "This module enhances the looting experience in AzerothCore by implementing Area-of-Effect (AOE) looting functionality. It allows players to loot multiple corpses at once within a defined radius, significantly improving quality of life for players.",
            Repository = "https://github.com/TerraByte-tbwps/mod-aoe-loot",
            Branch = "main",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-weather-vibe",
            Name = "Weather Vibe",
            Description = "Bring your world to life with mod_weather_vibe. This module gives each zone a distinct mood - misty mornings in Elwynn, a gloomy Duskwood that rumbles to life, biting Wintergrasp squalls, and rolling thunderheads over Stranglethorn. Weather no longer just flips; it evolves naturally over time with smooth intensity transitions, seasonal awareness, fog as a natural bridge between states, and regional syncing that makes the world feel alive and immersive.",
            Repository = "https://github.com/hermensbas/mod_weather_vibe",
            Branch = "main",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-dungeon-clear",
            Name = "Dungeon Clear",
            Description = "Autonomous tank-led 5-man dungeon clears for playerbots - the tank drives the party boss to boss, clears trash, and handles scripted events. Requires mod-playerbots. Pairs with the DungeonClear client addon for in-game control.",
            Repository = "https://github.com/TopHatMan/mod-dungeon-clear.git",
            Branch = "master",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-playerbot-dungeon-sim",
            Name = "Playerbot Dungeon Sim",
            Description = "Progression engine for random playerbots: level-appropriate 5-man dungeon runs via Dungeon Clear, then offscreen raid progression at cap with gearing from real loot tables. Requires Dungeon Clear and mod-playerbots. Autonomous bot runs may need a forked mod-dungeon-clear with StartAutonomousClear and DungeonClear.AllowAutonomousBotRuns = 1; apply the module SQL to the characters database after install.",
            Repository = "https://github.com/TopHatMan/mod-playerbot-dungeon-sim.git",
            Branch = "main",
            IsBuiltIn = true,
            RequiredModuleIds = ["mod-dungeon-clear"]
        },
        new()
        {
            Id = "mod-rotation",
            Name = "Rotation",
            Description = "One-button rotation for AzerothCore 3.3.5a (Playerbot fork compatible): one trigger spell, 10 classes, 30 specializations, Wowhead WotLK Classic priorities. No SQL; optional cosmetic MPQ is not installed.",
            Repository = "https://github.com/Maddnes95/mod-rotation",
            Branch = "main",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-pet-battle",
            Name = "Pet Battle System",
            Description = "Pet battles against world creatures and other players. Extra-data install copies the stock Interface/AddOns/PetBattleUI addon into the client.",
            Repository = "https://github.com/Faris-Kai/PetBattleSystem-AzerothCore",
            Branch = "main",
            IsBuiltIn = true
        },
        new()
        {
            Id = "aio",
            Name = "AIO",
            Description = "Rochet2 Addon Input/Output: server Lua for addon messaging (AIO_Server is copied into lua_scripts). The matching client addon already ships with the platform client. Requires ALE.",
            Repository = "https://github.com/Rochet2/AIO",
            Branch = "master",
            IsBuiltIn = true,
            RequiredModuleIds = ["mod-ale"]
        },
        new()
        {
            Id = "mod-guild-levels",
            Name = "Guild Levels",
            Description = "Guild experience, 25 levels, and Cataclysm-inspired perks. Extra-data install copies client_addon/GuildLevels into the client and lua/extensions/guild_levels into lua_scripts. Requires AIO (auto-selected).",
            Repository = "https://github.com/Old-Man-Warcraft/mod-guild-levels",
            Branch = "main",
            IsBuiltIn = true,
            RequiredModuleIds = ["aio"]
        },
        new()
        {
            Id = "mod-dynamic-loot-rates",
            Name = "Dynamic Loot Rates",
            Description = "Separate group/reference loot rates for dungeons and raids. Depends on AzerothCore PR #17456 being present on the core.",
            Repository = "https://github.com/hallgaeuer/mod-dynamic-loot-rates",
            Branch = "master",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-ip-challengesystem",
            Name = "IP Challenge System",
            Description = "Tiered opt-in challenges (Hardcore, SSF, Solo, Ascetic) for Individual Progression servers. Extra-data install applies the characters SQL under sql/ (not data/sql/). Requires Individual Progression.",
            Repository = "https://github.com/AzoghMartins/mod-ip-challengesystem",
            Branch = "main",
            IsBuiltIn = true,
            RequiredModuleIds = ["mod-individual-progression"]
        },
        new()
        {
            Id = "mod-character-services",
            Name = "Character Services",
            Description = "NPC for name, appearance, race, and faction changes, plus purchasing Individual Progression tiers. Spawn with .npc add 390011 after install. This fork requires Individual Progression.",
            Repository = "https://github.com/Badgermilk0/mod-character-services",
            Branch = "main",
            IsBuiltIn = true,
            RequiredModuleIds = ["mod-individual-progression"]
        },
        new()
        {
            Id = "mod-quest-loot-party",
            Name = "Quest Loot Party",
            Description = "When one party member loots a quest item, every eligible member receives it. Depends on AzerothCore PR #16509 being present on the core.",
            Repository = "https://github.com/pangolp/mod-quest-loot-party",
            Branch = "master",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-playerbots-artisans",
            Name = "Playerbots Artisans",
            Description = "Crafter playerbots advertise real learned recipes in Trade chat. Requires mod-playerbots.",
            Repository = "https://github.com/TopHatMan/mod-playerbots-artisans",
            Branch = "main",
            IsBuiltIn = true,
            RequiredModuleIds = ["mod-playerbots"]
        },
        new()
        {
            Id = "mod-world-events",
            Name = "World Events",
            Description = "Records boss kills, loot, level-ups, and achievements into one queryable characters table. Independent of other modules. Disabled by default until WorldEvents.Enable is set to 1.",
            Repository = "https://github.com/robbyczgw-cla/mod-world-events",
            Branch = "main",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-optimal-bot-raid",
            Name = "Optimal Bot Raid",
            Description = "Assembles a mathematically optimized playerbot raid (buff coverage, GearScore, role quotas) with .botraid assemble. Requires mod-playerbots.",
            Repository = "https://github.com/barnaclebarry/mod-optimal-bot-raid",
            Branch = "master",
            IsBuiltIn = true,
            RequiredModuleIds = ["mod-playerbots"]
        },
        new()
        {
            Id = "mod-world-buff-bots",
            Name = "World Buff Bots",
            Description = "Simulates classic world buff turn-ins (Warchief's Blessing, Rallying Cry of the Dragonslayer, Spirit of Zandalar) on independent randomized timers, announced and applied by online playerbots. Requires mod-playerbots.",
            Repository = "https://github.com/Rockhopper1776/mod-world-buff-bots",
            Branch = "main",
            IsBuiltIn = true,
            RequiredModuleIds = ["mod-playerbots"]
        },
        new()
        {
            Id = "mod-ollama-bot-buddy",
            Name = "Ollama Bot Buddy (Simple)",
            Description = "Experimental LLM-driven playerbot control via the Ollama API (questing, grinding, chat overrides). Requires mod-playerbots. Cannot be compiled together with Ollama Bot Buddy Advanced. Optional BuddyBotUI debug addon appears in Addons when this module is selected.",
            Repository = "https://github.com/DustinHendrickson/mod-ollama-bot-buddy",
            Branch = "main",
            IsBuiltIn = true,
            RequiredModuleIds = ["mod-playerbots"]
        },
        new()
        {
            Id = "mod-ollama-bot-buddy-advanced",
            Name = "Ollama Bot Buddy (Advanced)",
            Description = "Fero-Fero fork with bot memory and a heavier LLM loop. Requires mod-playerbots. Cannot be compiled together with Ollama Bot Buddy Simple. Optional BuddyBotUI debug addon appears in Addons when this module is selected.",
            Repository = "https://github.com/Fero-Fero/mod-ollama-bot-buddy",
            Branch = "main",
            IsBuiltIn = true,
            RequiredModuleIds = ["mod-playerbots"]
        },
        new()
        {
            Id = "clancentaur",
            Name = "Clan Centaur",
            Description = "Gelkis and Magram centaur reputation, quartermasters, and custom rewards in Desolace. Extra-data install imports DBClientFiles/Faction.csv and applies the world SQL under data/sql/world/base (not AzerothCore data/sql/db-world).",
            Repository = "https://github.com/araxiaonline/ClanCentaur",
            Branch = "main",
            IsBuiltIn = true
        },
        new()
        {
            Id = "delves",
            Name = "Delves",
            Description = "Custom solo and group delve maps for AzerothCore. Extra-data install imports DBC_CSV/DBFilesClient, packs MPQ/ into overlay patch-E.MPQ, seeds Server Map Files (maps/mmaps/vmaps), and copies lua_scripts. World SQL under data/sql/db-world is imported the normal way. Requires ALE.",
            Repository = "https://github.com/araxiaonline/Delves",
            Branch = "main",
            IsBuiltIn = true,
            RequiredModuleIds = ["mod-ale"]
        },
        new()
        {
            Id = "mod-profession-experience",
            Name = "Profession Experience",
            Description = "Awards experience when crafting or gathering with professions. Enable professions and tune XP amounts in the module conf.",
            Repository = "https://github.com/Tereneckla/mod-profession-experience",
            Branch = "main",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-missing-objectives",
            Name = "Missing Objectives",
            Description = "Adds quest_poi and quest_poi_points for classic dungeon and raid objectives that are not in sniffs. World SQL under data/sql/db-world is imported the normal way. Pair with a client map patch to see the dungeon maps.",
            Repository = "https://github.com/forumcorex/mod-missing-objectives",
            Branch = "master",
            IsBuiltIn = true
        }
    ];

    private static readonly HashSet<string> BuiltInIds =
        BuiltInModules.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ReadmeCandidates =
        ["README.md", "readme.md", "Readme.md", "README.markdown", "docs/README.md"];

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ModuleIdRegex();

    // Git ref/branch names we accept for clone/fetch. Deliberately strict: letters, digits and a few
    // path-safe punctuation characters, never starting with '-' (which would let a value masquerade as
    // a git option like --upload-pack). Used to block argument injection into the git CLI.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$")]
    private static partial Regex GitRefRegex();

    /// <summary>
    /// Validates a git branch/ref for safe use as a CLI argument. Throws <see cref="ArgumentException"/>
    /// when the value is empty, too long, contains disallowed characters, or could be parsed as an option.
    /// </summary>
    public static string ValidateGitRef(string? branch)
    {
        var value = (branch ?? string.Empty).Trim();
        if (!GitRefRegex().IsMatch(value) || value.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Branch/ref may only contain letters, digits, '.', '_', '/', '-', must not start with '-', and must not contain '..'.");
        }

        return value;
    }

    /// <summary>
    /// Validates a git repository URL for safe use as a CLI argument (absolute http(s) only, no leading
    /// '-'). Throws <see cref="ArgumentException"/> otherwise. Shared with the build pipeline.
    /// </summary>
    public static string ValidateGitRepository(string? repository)
    {
        var value = (repository ?? string.Empty).Trim();
        if (value.StartsWith('-')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Repository must be a valid http(s) URL.");
        }

        return value;
    }

    private readonly AzerothCoreDbContext _dbContext;
    private readonly IModulePackageStorage _packageStorage;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerTypeCatalog _serverTypeCatalog;

    public ModuleCatalogService(
        AzerothCoreDbContext dbContext,
        IModulePackageStorage packageStorage,
        IHttpClientFactory httpClientFactory,
        IServerTypeCatalog serverTypeCatalog)
    {
        _dbContext = dbContext;
        _packageStorage = packageStorage;
        _httpClientFactory = httpClientFactory;
        _serverTypeCatalog = serverTypeCatalog;
    }

    public async Task<IReadOnlyList<ModuleDto>> ListAsync(
        ServerType? serverType = null,
        CancellationToken cancellationToken = default)
    {
        var all = await ListAllAsync(cancellationToken);

        if (serverType is null)
        {
            return all;
        }

        // Apply the server-type catalog: keep only modules visible for this type and resolve any
        // per-type repository override (e.g. Individual Progression clones the Grimfeather fork). The
        // built-in modules are shared static instances, so overrides are applied to copies.
        var visible = new List<ModuleDto>();
        foreach (var module in all)
        {
            if (!_serverTypeCatalog.IsModuleVisible(module.Id, serverType.Value))
            {
                continue;
            }

            var (repository, branch) = _serverTypeCatalog.ResolveModuleRepository(
                module.Id, module.Repository, module.Branch, serverType.Value);

            visible.Add(
                repository == module.Repository && branch == module.Branch
                    ? module
                    : CloneWithRepository(module, repository, branch));
        }

        return visible;
    }

    private static ModuleDto CloneWithRepository(ModuleDto source, string repository, string branch) => new()
    {
        Id = source.Id,
        SourceType = source.SourceType,
        Name = source.Name,
        Description = source.Description,
        Repository = repository,
        Branch = branch,
        IsBuiltIn = source.IsBuiltIn,
        Recommended = source.Recommended,
        RequiredModuleIds = source.RequiredModuleIds
    };

    public async Task<IReadOnlyList<ModuleDto>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        var custom = await _dbContext.CatalogModules
            .OrderBy(m => m.Name)
            .Select(m => ToDto(m))
            .ToListAsync(cancellationToken);

        return BuiltInModules.Concat(custom).ToList();
    }

    public async Task<ModuleDto> CreateAsync(SaveModuleRequest request, CancellationToken cancellationToken = default)
    {
        var id = await ValidateNewIdAsync(request.Id, cancellationToken);

        var entity = new CatalogModuleEntity { Id = id, SourceType = ModuleSource.Git, CreatedAt = DateTime.UtcNow };
        ApplyMetadata(entity, request);
        ApplyGitSource(entity, request);

        _dbContext.CatalogModules.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(entity);
    }

    public async Task<ModuleDto> CreateFromPackageAsync(
        SaveModuleRequest request,
        string fileName,
        Stream zipContent,
        CancellationToken cancellationToken = default)
    {
        EnsureZip(fileName);
        var id = await ValidateNewIdAsync(request.Id, cancellationToken);

        var entity = new CatalogModuleEntity
        {
            Id = id,
            SourceType = ModuleSource.Package,
            Repository = string.Empty,
            Branch = string.Empty,
            CreatedAt = DateTime.UtcNow
        };
        ApplyMetadata(entity, request);

        await _packageStorage.SavePackageAsync(id, zipContent, cancellationToken);

        _dbContext.CatalogModules.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(entity);
    }

    public async Task<ModuleDto> ReplacePackageAsync(
        string moduleId,
        string fileName,
        Stream zipContent,
        CancellationToken cancellationToken = default)
    {
        EnsureZip(fileName);

        var entity = await GetCustomModuleAsync(moduleId, cancellationToken);
        if (entity.SourceType != ModuleSource.Package)
        {
            throw new InvalidOperationException("Only package modules can have their package replaced.");
        }

        await _packageStorage.SavePackageAsync(moduleId, zipContent, cancellationToken);
        return ToDto(entity);
    }

    public async Task<ModuleDto> UpdateAsync(string moduleId, SaveModuleRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await GetCustomModuleAsync(moduleId, cancellationToken);

        ApplyMetadata(entity, request);
        if (entity.SourceType == ModuleSource.Git)
        {
            ApplyGitSource(entity, request);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task DeleteAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var entity = await GetCustomModuleAsync(moduleId, cancellationToken);

        _dbContext.CatalogModules.Remove(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (entity.SourceType == ModuleSource.Package)
        {
            _packageStorage.DeletePackage(moduleId);
        }
    }

    public async Task<ModuleReadmeDto> GetReadmeAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var result = new ModuleReadmeDto { ModuleId = moduleId };

        var module = (await ListAllAsync(cancellationToken)).FirstOrDefault(m => m.Id == moduleId)
            ?? throw new KeyNotFoundException($"Module not found: {moduleId}");

        if (module.SourceType == ModuleSource.Package)
        {
            var content = await _packageStorage.ReadReadmeAsync(moduleId, cancellationToken);
            if (!string.IsNullOrEmpty(content))
            {
                result.Found = true;
                result.Content = content;
            }
            return result;
        }

        // Git module: fetch the README from the raw GitHub host.
        var parsed = ParseGitHub(module.Repository);
        if (parsed == null)
        {
            return result;
        }

        var (owner, repo) = parsed.Value;
        var branch = string.IsNullOrWhiteSpace(module.Branch) ? "master" : module.Branch;
        var client = _httpClientFactory.CreateClient();

        foreach (var candidate in ReadmeCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{candidate}";
            try
            {
                var response = await client.GetAsync(url, cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    result.Found = true;
                    result.Content = await response.Content.ReadAsStringAsync(cancellationToken);
                    result.BaseUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/";
                    return result;
                }
            }
            catch (HttpRequestException)
            {
                // try the next candidate / return not found
            }
        }

        return result;
    }

    // ===== Helpers =====

    private async Task<CatalogModuleEntity> GetCustomModuleAsync(string moduleId, CancellationToken cancellationToken)
    {
        if (BuiltInIds.Contains(moduleId))
        {
            throw new InvalidOperationException("Built-in modules cannot be edited or deleted.");
        }

        return await _dbContext.CatalogModules.SingleOrDefaultAsync(m => m.Id == moduleId, cancellationToken)
            ?? throw new KeyNotFoundException($"Module not found: {moduleId}");
    }

    private async Task<string> ValidateNewIdAsync(string? requestedId, CancellationToken cancellationToken)
    {
        var id = (requestedId ?? string.Empty).Trim();
        if (!ModuleIdRegex().IsMatch(id))
        {
            throw new ArgumentException("Module id is required and may only contain letters, digits, '.', '_' and '-'.");
        }

        if (BuiltInIds.Contains(id))
        {
            throw new InvalidOperationException($"'{id}' is a built-in module id and cannot be reused.");
        }

        if (await _dbContext.CatalogModules.AnyAsync(m => m.Id == id, cancellationToken))
        {
            throw new InvalidOperationException($"A module with id '{id}' already exists.");
        }

        return id;
    }

    private static void EnsureZip(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Module packages must be uploaded as a .zip archive.");
        }
    }

    private static void ApplyMetadata(CatalogModuleEntity entity, SaveModuleRequest request)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Module name is required.");
        }

        entity.Name = name;
        entity.Description = (request.Description ?? string.Empty).Trim();
    }

    private static void ApplyGitSource(CatalogModuleEntity entity, SaveModuleRequest request)
    {
        entity.Repository = ValidateGitRepository(request.Repository);
        entity.Branch = string.IsNullOrWhiteSpace(request.Branch) ? "master" : ValidateGitRef(request.Branch);
    }

    private static (string owner, string repo)? ParseGitHub(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return null;
        }

        var url = repositoryUrl.Trim().TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^4];
        }

        const string httpsPrefix = "https://github.com/";
        const string sshPrefix = "git@github.com:";

        string? path = null;
        if (url.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = url[httpsPrefix.Length..];
        }
        else if (url.StartsWith(sshPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = url[sshPrefix.Length..];
        }

        if (path == null)
        {
            return null;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? (parts[0], parts[1]) : null;
    }

    private static ModuleDto ToDto(CatalogModuleEntity entity) => new()
    {
        Id = entity.Id,
        SourceType = entity.SourceType,
        Name = entity.Name,
        Description = entity.Description,
        Repository = entity.Repository,
        Branch = entity.Branch,
        IsBuiltIn = false,
        Recommended = false
    };
}

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
            Description = "A faithful backport of the Mists of Pandaria Black Market Auction House assets and functionality to AzerothCore 3.3.5 using the Eluna Lua engine.",
            Repository = "https://github.com/Youpeoples/Black-Market-Auction-House.git",
            Branch = "main",
            IsBuiltIn = true
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
            Description = "Mount satisfaction system for AzerothCore 3.3.5a. Mounts must be fed to maintain full speed — similar to the hunter pet happiness mechanic.",
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
            Description = "Bring your world to life with mod_weather_vibe. This module gives each zone a distinct mood — misty mornings in Elwynn, a gloomy Duskwood that rumbles to life, biting Wintergrasp squalls, and rolling thunderheads over Stranglethorn. Weather no longer just flips; it evolves naturally over time with smooth intensity transitions, seasonal awareness, fog as a natural bridge between states, and regional syncing that makes the world feel alive and immersive.",
            Repository = "https://github.com/hermensbas/mod_weather_vibe",
            Branch = "main",
            IsBuiltIn = true
        },
        new()
        {
            Id = "mod-dungeon-clear",
            Name = "Dungeon Clear",
            Description = "Autonomous tank-led 5-man dungeon clears for playerbots — the tank drives the party boss to boss, clears trash, and handles scripted events. Requires mod-playerbots. Pairs with the DungeonClear client addon for in-game control.",
            Repository = "https://github.com/jrad7/mod-dungeon-clear.git",
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

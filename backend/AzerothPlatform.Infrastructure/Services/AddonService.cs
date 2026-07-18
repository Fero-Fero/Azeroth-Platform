using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Services.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Serves WoW addons through the launcher by managing files under a client root's
/// <c>game/Interface/AddOns/</c> directory and rescanning the client manifest afterwards.
/// Addons are "managed" files (see ManagedPrefixes), so the launcher auto-installs, updates and
/// prunes them, while never touching a player's own locally-installed addons.
/// </summary>
public sealed class AddonService : IAddonService
{
    private const string GameDirName = "game";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // WoW addons live here, relative to the client's game/ directory.
    private static readonly string AddonsRelativeDir = Path.Combine("Interface", "AddOns");

    /// <summary>
    /// Curated, statically-defined addon catalog (mirrors the module catalog's <c>BuiltInModules</c>).
    /// All entries target the 3.3.5a (WotLK) client and are fetched as <c>.zip</c> archives on install.
    /// <see cref="AddonCatalogEntryDto.Folders"/> lists the addon folder(s) each archive installs so the
    /// UI can report install status.
    /// </summary>
    private static readonly IReadOnlyList<AddonCatalogEntryDto> BuiltInAddons =
    [
        new()
        {
            Id = "questie-335",
            Name = "Questie (3.3.5a)",
            Description = "Quest helper that pins quest objectives, turn-ins, and available quests on the map and minimap. AzerothCore-compatible 3.3.5a port.",
            Category = "Quests",
            DownloadUrl = "https://github.com/Aldori15/Questie/archive/refs/heads/335.zip",
            Website = "https://github.com/Aldori15/Questie",
            Folders = ["Questie-335"],
            Recommended = true,
        },
        new()
        {
            Id = "pfquest-wotlk",
            Name = "pfQuest (WotLK)",
            Description = "Lightweight quest helper and database browser. Automatically pins relevant NPCs, mobs, and objects when you pick up a quest.",
            Category = "Quests",
            DownloadUrl = "https://github.com/shagu/pfQuest/releases/latest/download/pfQuest-full-wotlk.zip",
            Website = "https://github.com/shagu/pfQuest",
            Folders = ["pfQuest-wotlk"],
        },
        new()
        {
            Id = "dbm-wotlk",
            Name = "Deadly Boss Mods (3.3.5a)",
            Description = "Boss timers and warnings for raids and dungeons. Retail-accurate backport maintained for the 3.3.5a client.",
            Category = "Raiding",
            DownloadUrl = "https://github.com/Zidras/DBM-Warmane/archive/refs/heads/main.zip",
            Website = "https://github.com/Zidras/DBM-Warmane",
            Folders = ["DBM-Core", "DBM-GUI"],
        },
        new()
        {
            Id = "bartender4",
            Name = "Bartender4",
            Description = "Full action-bar replacement with extensive customization of your action and related bars.",
            Category = "UI",
            DownloadUrl = "https://github.com/sirus-addons/Bartender4/archive/refs/heads/master.zip",
            Website = "https://github.com/sirus-addons/Bartender4",
            Folders = ["Bartender4"],
        },
        new()
        {
            Id = "moveanything",
            Name = "MoveAnything",
            Description = "Move, scale, hide, and adjust the transparency of almost any part of the default WoW interface.",
            Category = "UI",
            DownloadUrl = "https://github.com/sirus-addons/MoveAnything/archive/refs/heads/master.zip",
            Website = "https://github.com/sirus-addons/MoveAnything",
            Folders = ["MoveAnything"],
        },
        new()
        {
            Id = "dungeon-clear-addon",
            Name = "Dungeon Clear",
            Description = "In-game panel for mod-dungeon-clear — start, pause, skip, and monitor autonomous tank-led dungeon clears without typing chat commands.",
            Category = "UI",
            DownloadUrl = "https://github.com/jrad7/mod-dungeon-clear-addon/archive/refs/heads/master.zip",
            Website = "https://github.com/jrad7/mod-dungeon-clear-addon",
            Folders = ["DungeonClear"],
            InstallAsFolder = "DungeonClear",
            RelatedModuleIds = ["mod-dungeon-clear"],
        },
        new()
        {
            Id = "atlas-loot-individual-progression",
            Name = "AtlasLoot Individual Progression",
            Description = "Restored loot tables for Naxxramas 40-man, Onyxia's Lair 40-man, and Kazzak for progressive servers running through Vanilla, TBC, and WotLK.",
            Category = "UI",
            DownloadUrl = "https://github.com/Day36512/Atlas-Loot-Individual-Progression-3.3.5/raw/main/Atlas-Loot-Individual-Progression-3.3.5.zip",
            Website = "https://github.com/Day36512/Atlas-Loot-Individual-Progression-3.3.5",
            Folders = ["AtlasLoot"],
            RelatedModuleIds = ["mod-individual-progression"],
            RelatedServerTypes = [nameof(ServerType.IndividualProgression)],
        },
        new()
        {
            Id = "wotlk-storyline",
            Name = "Storyline (WotLK)",
            Description = "Immersive quest dialogue UI — backport of the Storyline addon for WotLK 3.3.5a.",
            Category = "Quests",
            DownloadUrl = "https://github.com/Fero-Fero/WotLK-Storyline/archive/refs/heads/master.zip",
            Website = "https://github.com/Fero-Fero/WotLK-Storyline",
            Folders = ["Storyline"],
            InstallAsFolder = "Storyline",
        },
        new()
        {
            Id = "ai-voiceover",
            Name = "AI VoiceOver",
            Description = "AI-generated voice-over for quest dialogues on the 3.3.5a client. Select one or more data packs below for the expansions you need.",
            Category = "Quests",
            DownloadUrl = "https://github.com/celguar/wow-voiceover/releases/download/1.5.0/AI_VoiceOver-WoW_3.3.5.zip",
            Website = "https://github.com/celguar/wow-voiceover",
            Folders = ["AI_VoiceOver"],
        },
        new()
        {
            Id = "ai-voiceover-data-vanilla",
            Name = "AI VoiceOver — Vanilla Sounds",
            Description = "Voice-over data pack for Vanilla quest content (~1.4 GB).",
            Category = "Quests",
            DownloadUrl = "https://github.com/celguar/wow-voiceover/releases/download/1.5.0/AI_VoiceOverData_Vanilla.zip",
            Website = "https://github.com/celguar/wow-voiceover",
            Folders = ["AI_VoiceOverData_Vanilla"],
            ParentAddonId = "ai-voiceover",
        },
        new()
        {
            Id = "ai-voiceover-data-tbc",
            Name = "AI VoiceOver — TBC Sounds",
            Description = "Voice-over data pack for The Burning Crusade quest content (~700 MB).",
            Category = "Quests",
            DownloadUrl = "https://github.com/celguar/wow-voiceover/releases/download/1.5.0/AI_VoiceOverData_TBC.zip",
            Website = "https://github.com/celguar/wow-voiceover",
            Folders = ["AI_VoiceOverData_TBC"],
            ParentAddonId = "ai-voiceover",
        },
        new()
        {
            Id = "ai-voiceover-data-wotlk",
            Name = "AI VoiceOver — WotLK Sounds",
            Description = "Voice-over data pack for Wrath of the Lich King quest content (~750 MB).",
            Category = "Quests",
            DownloadUrl = "https://github.com/celguar/wow-voiceover/releases/download/1.5.0/AI_VoiceOverData_WoTLK.zip",
            Website = "https://github.com/celguar/wow-voiceover",
            Folders = ["AI_VoiceOverData_WoTLK"],
            ParentAddonId = "ai-voiceover",
        },
    ];

    private readonly ClientDistributionOptions _clientOptions;
    private readonly DockerOptions _dockerOptions;
    private readonly IClientDistributionService _clientDistribution;
    private readonly IStackLauncherService _stackLauncher;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AddonService> _logger;
    private readonly AzerothCoreDbContext _dbContext;

    public AddonService(
        IOptions<ClientDistributionOptions> clientOptions,
        IOptions<DockerOptions> dockerOptions,
        IClientDistributionService clientDistribution,
        IStackLauncherService stackLauncher,
        IHttpClientFactory httpClientFactory,
        ILogger<AddonService> logger,
        AzerothCoreDbContext dbContext)
    {
        _clientOptions = clientOptions.Value;
        _dockerOptions = dockerOptions.Value;
        _clientDistribution = clientDistribution;
        _stackLauncher = stackLauncher;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _dbContext = dbContext;
    }

    public Task<AddonListDto> ListAsync(string? stackId, CancellationToken cancellationToken = default)
    {
        var addonsDir = ResolveAddonsDir(stackId);
        return Task.FromResult(BuildList(stackId, addonsDir));
    }

    public async Task<AddonListDto> UploadZipAsync(string? stackId, string fileName, Stream zipContent, CancellationToken cancellationToken = default)
    {
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Addons must be uploaded as a .zip archive.");
        }

        var addonsDir = ResolveAddonsDir(stackId);
        Directory.CreateDirectory(addonsDir);

        var extracted = await ExtractZipAsync(zipContent, addonsDir, cancellationToken);
        _logger.LogInformation(
            "Extracted {Count} addon file(s) from {FileName} into {Dir}", extracted, fileName, addonsDir);

        await RescanAsync(stackId, cancellationToken);
        return BuildList(stackId, addonsDir);
    }

    public async Task<AddonListDto> DeleteAsync(string? stackId, string addonName, CancellationToken cancellationToken = default)
    {
        var addonsDir = ResolveAddonsDir(stackId);
        var target = SafeResolveChild(addonsDir, addonName);

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
        else if (File.Exists(target))
        {
            File.Delete(target);
        }
        else
        {
            throw new FileNotFoundException($"Addon not found: {addonName}");
        }

        _logger.LogInformation("Deleted addon {AddonName} from {Dir}", addonName, addonsDir);

        await RescanAsync(stackId, cancellationToken);
        return BuildList(stackId, addonsDir);
    }

    public async Task<IReadOnlyList<AddonCatalogEntryDto>> GetCatalogAsync(string? stackId, CancellationToken cancellationToken = default)
    {
        var addonsDir = ResolveAddonsDir(stackId);
        var (stackModuleIds, stackServerType) = await LoadStackContextAsync(stackId, cancellationToken);

        var catalog = BuiltInAddons
            .Select(a =>
            {
                var installed = a.Folders.Count > 0 && a.Folders.Any(f => Directory.Exists(Path.Combine(addonsDir, f)));
                var suggested = !string.IsNullOrWhiteSpace(stackId)
                    && !installed
                    && IsSuggestedForStack(a, stackModuleIds, stackServerType);

                return new AddonCatalogEntryDto
                {
                    Id = a.Id,
                    Name = a.Name,
                    Description = a.Description,
                    Category = a.Category,
                    DownloadUrl = a.DownloadUrl,
                    Website = a.Website,
                    IsBuiltIn = a.IsBuiltIn,
                    Folders = a.Folders,
                    Installed = installed,
                    Recommended = a.Recommended,
                    RelatedModuleIds = a.RelatedModuleIds,
                    RelatedServerTypes = a.RelatedServerTypes,
                    Suggested = suggested,
                    ParentAddonId = a.ParentAddonId,
                };
            })
            .OrderByDescending(a => a.Recommended)
            .ThenByDescending(a => a.Suggested)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return catalog;
    }

    public async Task<AddonListDto> InstallFromCatalogAsync(string? stackId, string addonId, CancellationToken cancellationToken = default)
    {
        var entry = BuiltInAddons.FirstOrDefault(a => string.Equals(a.Id, addonId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Unknown catalog addon: {addonId}");

        // Only https downloads from the trusted static catalog are allowed (no arbitrary/user URLs).
        if (!Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"Catalog addon '{addonId}' has an invalid download URL.");
        }

        var addonsDir = ResolveAddonsDir(stackId);
        Directory.CreateDirectory(addonsDir);

        var tempZip = Path.GetTempFileName();
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(5);
            using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var src = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var dst = File.Create(tempZip);
                await src.CopyToAsync(dst, cancellationToken);
            }

            var installed = InstallArchive(tempZip, addonsDir, entry.InstallAsFolder, cancellationToken);
            _logger.LogInformation(
                "Installed catalog addon {AddonId} ({Count} folder(s)) into {Dir}", addonId, installed, addonsDir);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* best effort */ }
        }

        await RescanAsync(stackId, cancellationToken);
        return BuildList(stackId, addonsDir);
    }

    // ===== Helpers =====

    private async Task<(HashSet<string> ModuleIds, ServerType? ServerType)> LoadStackContextAsync(
        string? stackId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stackId))
        {
            return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);
        }

        var stack = await _dbContext.ManagedStacks
            .AsNoTracking()
            .Where(entry => entry.Id == stackId)
            .Select(entry => new { entry.ModuleIdsJson, entry.ServerType })
            .SingleOrDefaultAsync(cancellationToken);

        if (stack is null)
        {
            return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);
        }

        HashSet<string> moduleIds;
        if (string.IsNullOrWhiteSpace(stack.ModuleIdsJson))
        {
            moduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson, JsonOptions) ?? [];
            moduleIds = parsed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return (moduleIds, stack.ServerType);
    }

    private static bool IsSuggestedForStack(
        AddonCatalogEntryDto entry,
        HashSet<string> stackModuleIds,
        ServerType? stackServerType)
    {
        if (entry.RelatedModuleIds.Count > 0
            && entry.RelatedModuleIds.Any(id => stackModuleIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (stackServerType is null || entry.RelatedServerTypes.Count == 0)
        {
            return false;
        }

        var serverTypeName = stackServerType.Value.ToString();
        return entry.RelatedServerTypes.Any(
            related => string.Equals(related, serverTypeName, StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveAddonsDir(string? stackId)
    {
        string clientRoot;
        if (string.IsNullOrWhiteSpace(stackId))
        {
            clientRoot = _clientOptions.RootPath;
        }
        else
        {
            var baseDir = Path.IsPathRooted(_dockerOptions.BuildsPath)
                ? _dockerOptions.BuildsPath
                : Path.GetFullPath(_dockerOptions.BuildsPath);
            clientRoot = Path.Combine(baseDir, stackId, MigrationLayout.ClientDirName);
        }

        return Path.Combine(clientRoot, GameDirName, AddonsRelativeDir);
    }

    private Task RescanAsync(string? stackId, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(stackId)
            ? _clientDistribution.RescanAsync(cancellationToken)
            : _stackLauncher.RescanAsync(stackId, cancellationToken);

    private static AddonListDto BuildList(string? stackId, string addonsDir)
    {
        var dto = new AddonListDto
        {
            IsStackScoped = !string.IsNullOrWhiteSpace(stackId),
            StackId = string.IsNullOrWhiteSpace(stackId) ? null : stackId
        };

        if (!Directory.Exists(addonsDir))
        {
            return dto;
        }

        var recommendedFolders = BuiltInAddons
            .Where(a => a.Recommended)
            .SelectMany(a => a.Folders)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in Directory.EnumerateDirectories(addonsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);

            // Default client addons (the Blizzard_* UI modules and AIO) ship with every WoW client, so
            // they're just noise in the management list — hide them so admins only see addons they
            // actually added.
            if (IsDefaultAddon(name))
            {
                continue;
            }

            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
            var size = files.Sum(f => new FileInfo(f).Length);
            dto.Addons.Add(new AddonSummaryDto
            {
                Name = name,
                FileCount = files.Count,
                TotalSize = size,
                Recommended = recommendedFolders.Contains(name)
            });
        }

        dto.Addons = dto.Addons
            .OrderByDescending(a => a.Recommended)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        dto.TotalSize = dto.Addons.Sum(a => a.TotalSize);
        return dto;
    }

    /// <summary>
    /// True for addons that ship with the base WoW client (the <c>Blizzard_*</c> default UI modules)
    /// and the ubiquitous <c>AIO</c> framework addon. These are hidden from the management list because
    /// they aren't admin-added content.
    /// </summary>
    private static bool IsDefaultAddon(string name) =>
        name.StartsWith("Blizzard_", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Blizzard", StringComparison.OrdinalIgnoreCase)
        || name.Equals("AIO", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("AIO_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extracts a zip into <paramref name="destDir"/>, guarding against path traversal (zip-slip).</summary>
    private static async Task<int> ExtractZipAsync(Stream zipContent, string destDir, CancellationToken cancellationToken)
    {
        // Buffer to a temp file so ZipArchive can seek even when the request body is non-seekable.
        var tempFile = Path.GetTempFileName();
        try
        {
            await using (var fs = File.Create(tempFile))
            {
                await zipContent.CopyToAsync(fs, cancellationToken);
            }

            var extractedFiles = ExtractArchiveToDir(tempFile, destDir, "addons directory", cancellationToken);
            if (extractedFiles == 0)
            {
                throw new ArgumentException("The uploaded archive contained no files.");
            }

            return extractedFiles;
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Extracts every entry of <paramref name="zipPath"/> into <paramref name="destDir"/>, rejecting any
    /// entry whose resolved path escapes the destination (zip-slip). Returns the number of files written.
    /// </summary>
    private static int ExtractArchiveToDir(string zipPath, string destDir, string label, CancellationToken cancellationToken)
    {
        var destFull = Path.GetFullPath(destDir);
        var destWithSep = destFull.EndsWith(Path.DirectorySeparatorChar)
            ? destFull
            : destFull + Path.DirectorySeparatorChar;

        var extractedFiles = 0;
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var target = Path.GetFullPath(Path.Combine(destFull, entry.FullName));
                if (!target.StartsWith(destWithSep, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Zip entry escapes the {label}: {entry.FullName}");
                }

                // Directory entry (name is empty for "foo/" entries).
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                extractedFiles++;
            }
        }
        catch (InvalidDataException)
        {
            throw new ArgumentException("The file is not a valid .zip archive.");
        }

        return extractedFiles;
    }

    /// <summary>
    /// Extracts an addon archive into <paramref name="destDir"/>, handling GitHub-style zips that wrap
    /// one or more addon folders inside a top-level directory. Addon folders are detected by a contained
    /// <c>.toc</c> file; each is moved to <c>Interface/AddOns/&lt;folder&gt;</c>. Returns the folder count.
    /// </summary>
    private static int InstallArchive(string zipFile, string destDir, string? installAsFolder, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(Path.GetTempPath(), "addon-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            ExtractArchiveToDir(zipFile, staging, "addon archive", cancellationToken);

            // Addon folders are directories that directly contain a *.toc file. Only take the shallowest
            // ones so we don't descend into a valid addon that itself has nested sub-addons.
            var addonRoots = FindAddonRoots(staging);

            // Fallback for archives without .toc files: install the top-level directories (unwrapping a
            // single GitHub wrapper folder if present).
            if (addonRoots.Count == 0)
            {
                var topDirs = Directory.EnumerateDirectories(staging).ToList();
                addonRoots = topDirs.Count == 1
                    ? Directory.EnumerateDirectories(topDirs[0]).ToList()
                    : topDirs;
            }

            if (addonRoots.Count == 0)
            {
                throw new ArgumentException("The addon archive contained no installable addon folders.");
            }

            foreach (var root in addonRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = !string.IsNullOrWhiteSpace(installAsFolder)
                    ? installAsFolder.Trim()
                    : Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var target = SafeResolveChild(destDir, name);
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                MoveDirectory(root, target);
            }

            return addonRoots.Count;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Moves a directory, falling back to a recursive copy when the source and destination live on
    /// different volumes (the staging temp dir and the data volume often do inside a container).
    /// </summary>
    private static void MoveDirectory(string source, string dest)
    {
        try
        {
            Directory.Move(source, dest);
        }
        catch (IOException)
        {
            CopyDirectory(source, dest);
            Directory.Delete(source, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }

    /// <summary>Shallowest directories under <paramref name="root"/> that directly contain a .toc file.</summary>
    private static List<string> FindAddonRoots(string root)
    {
        var results = new List<string>();

        void Walk(string dir)
        {
            var hasToc = Directory.EnumerateFiles(dir, "*.toc").Any();
            if (hasToc)
            {
                results.Add(dir);
                return; // don't descend into an addon folder
            }

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                Walk(sub);
            }
        }

        Walk(root);
        return results;
    }

    /// <summary>Resolves a single child name under a directory, rejecting traversal / nested paths.</summary>
    private static string SafeResolveChild(string parentDir, string childName)
    {
        if (string.IsNullOrWhiteSpace(childName))
        {
            throw new ArgumentException("Addon name is required.");
        }

        // Only a single path segment is allowed (no slashes, no "..").
        if (childName.Contains('/') || childName.Contains('\\') || childName == ".." || childName == ".")
        {
            throw new ArgumentException($"Invalid addon name: {childName}");
        }

        var parentFull = Path.GetFullPath(parentDir);
        var candidate = Path.GetFullPath(Path.Combine(parentFull, childName));

        var parentWithSep = parentFull.EndsWith(Path.DirectorySeparatorChar)
            ? parentFull
            : parentFull + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(parentWithSep, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid addon name: {childName}");
        }

        return candidate;
    }
}

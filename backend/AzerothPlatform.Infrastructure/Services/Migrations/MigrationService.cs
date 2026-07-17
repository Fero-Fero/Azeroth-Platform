using System.Text.Json;
using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.IndividualProgression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.Migrations;

/// <summary>
/// Manages per-stack migration/patch folders and applies patches incrementally: SQL to the
/// databases, DBC via a Wine-packaged WDBXEditor against a cumulative server_dbc baseline, map
/// overrides into the data volume, and MPQ files into the per-stack launcher client.
/// </summary>
public sealed partial class MigrationService : IMigrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static readonly HashSet<string> SqlCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "sql/world", "sql/auth", "sql/characters"
    };

    private static readonly string[] ExpansionOrder = { "classic", "tbc", "wotlk", "custom" };

    /// <summary>
    /// An apply lock older than this is treated as stale (the manager likely crashed mid-apply) and may
    /// be reclaimed by a new run. Generous because applies can rebuild images and run WDBX imports.
    /// </summary>
    internal static readonly TimeSpan ApplyLockStaleAfter = TimeSpan.FromMinutes(60);

    /// <summary>Whether a stack currently holds a live (non-stale) apply lock.</summary>
    internal static bool IsApplyLockLive(ManagedStackEntity stack) =>
        stack.ApplyingPatchKey is not null
        && stack.ApplyStartedAt is not null
        && stack.ApplyStartedAt.Value > DateTime.UtcNow - ApplyLockStaleAfter;

    private readonly AzerothCoreDbContext _dbContext;
    private readonly DockerOptions _dockerOptions;
    private readonly MigrationOptions _migrationOptions;
    private readonly IClientDistributionService _clientDistribution;
    private readonly IMigrationImageService _imageService;
    private readonly IRemoteEngineService _remoteEngine;
    private readonly IIndividualProgressionSyncService _individualProgression;
    private readonly IServerConfigService _serverConfig;
    private readonly IStackRegistryService _stackRegistry;
    private readonly ILauncherPortalService _launcherPortal;
    private readonly ClientServerOptions _clientServerOptions;
    private readonly ILogger<MigrationService> _logger;

    public MigrationService(
        AzerothCoreDbContext dbContext,
        IOptions<DockerOptions> dockerOptions,
        IOptions<MigrationOptions> migrationOptions,
        IClientDistributionService clientDistribution,
        IMigrationImageService imageService,
        IRemoteEngineService remoteEngine,
        IIndividualProgressionSyncService individualProgression,
        IServerConfigService serverConfig,
        IStackRegistryService stackRegistry,
        ILauncherPortalService launcherPortal,
        IOptions<ClientServerOptions> clientServerOptions,
        ILogger<MigrationService> logger)
    {
        _dbContext = dbContext;
        _dockerOptions = dockerOptions.Value;
        _migrationOptions = migrationOptions.Value;
        _clientDistribution = clientDistribution;
        _imageService = imageService;
        _remoteEngine = remoteEngine;
        _individualProgression = individualProgression;
        _serverConfig = serverConfig;
        _stackRegistry = stackRegistry;
        _launcherPortal = launcherPortal;
        _clientServerOptions = clientServerOptions.Value;
        _logger = logger;
    }

    private string BaseDir => Path.IsPathRooted(_dockerOptions.BuildsPath)
        ? _dockerOptions.BuildsPath
        : Path.GetFullPath(_dockerOptions.BuildsPath);

    private string GetStackRoot(string stackId) => Path.Combine(BaseDir, stackId);

    // ===== Overview / details =====

    public async Task<byte[]> GetPatchTemplateArchiveAsync(string stackId, CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        return MigrationLayout.CreatePatchTemplateArchive();
    }

    public async Task<MigrationOverviewDto> GetOverviewAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        // Default placeholder patches are seeded once in EnsureScaffold at stack build. Do not recreate
        // them here — IP bootstrap removes them, and operators delete unapplied patches from the browser.
        Directory.CreateDirectory(MigrationLayout.MigrationsRoot(stackRoot));

        var patches = EnumeratePatches(stackRoot);
        var currentLevel = stack.AppliedPatchLevel;
        var nextLevel = patches
            .Where(patch => patch.Level > currentLevel)
            .Select(patch => (int?)patch.Level)
            .Min();

        var appliedAt = ParseAppliedPatches(stack.AppliedPatchesJson);
        var moduleIds = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson, JsonOptions) ?? [];
        var hasIpModule = _individualProgression.StackHasModule(moduleIds);
        var ipSettings = hasIpModule
            ? await _individualProgression.GetSettingsAsync(stackId, cancellationToken)
            : null;

        var summaries = new List<PatchSummaryDto>();
        foreach (var patch in patches)
        {
            MigrationLayout.SeedPatchDescriptionIfMissing(stackRoot, patch.Key);
            var (sql, dbc, map, mpq) = CountFiles(stackRoot, patch.Key);
            var metadata = hasIpModule
                ? await _individualProgression.ReadPatchMetadataAsync(stackRoot, patch.Key)
                : null;

            summaries.Add(new PatchSummaryDto
            {
                Key = patch.Key,
                Index = patch.Index.ToIndexString(),
                Level = patch.Level,
                Name = patch.DisplayName,
                Status = ResolveStatus(patch.Level, currentLevel, nextLevel),
                SqlCount = sql,
                DbcCount = dbc,
                MapCount = map,
                MpqCount = mpq,
                Description = MigrationLayout.ReadPatchDescription(stackRoot, patch.Key),
                AppliedAt = appliedAt.TryGetValue(patch.Key, out var at) ? at : null,
                ProgressionState = metadata?.State,
                ProgressionSlug = metadata?.Slug,
                ProgressionTitle = patch.DisplayName,
                IncrementsProgression = metadata?.IncrementsProgression,
            });
        }

        var applying = IsApplyLockLive(stack);

        var patchCount = hasIpModule ? _individualProgression.CountProgressionPatches(stackRoot) : 0;
        var validationRequired = hasIpModule && (ipSettings?.Bootstrapped ?? false);
        var validationCurrent = validationRequired
            && ipSettings is not null
            && IndividualProgressionBuildFingerprint.IsCurrent(ipSettings, stack);

        return new MigrationOverviewDto
        {
            StackId = stackId,
            CurrentLevel = currentLevel,
            CurrentIndex = FormatCurrentIndex(currentLevel),
            BaselineInitialized = IsBaselineInitialized(stackRoot),
            IsApplying = applying,
            ApplyingPatchKey = applying ? stack.ApplyingPatchKey : null,
            Patches = summaries,
            HasIndividualProgressionModule = hasIpModule,
            IndividualProgressionBootstrapped = ipSettings?.Bootstrapped ?? false,
            IndividualProgressionValidationRequired = validationRequired,
            IndividualProgressionValidationCurrent = validationCurrent,
            IndividualProgressionValidationPassedAt = ipSettings?.ValidationPassedAt,
            IndividualProgressionPatchCount = patchCount,
            IndividualProgressionExpectedPatchCount = hasIpModule
                ? _individualProgression.GetExpectedProgressionPatchCount(stackId)
                : 0,
        };
    }

    public async Task<PatchDetailsDto> GetPatchAsync(string stackId, string patchKey, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var patch = RequirePatch(stackRoot, patchKey);

        var patches = EnumeratePatches(stackRoot);
        var nextLevel = patches
            .Where(p => p.Level > stack.AppliedPatchLevel)
            .Select(p => (int?)p.Level)
            .Min();

        var appliedAt = ParseAppliedPatches(stack.AppliedPatchesJson);
        var metadata = await _individualProgression.ReadPatchMetadataAsync(stackRoot, patch.Key);
        LauncherNewsItemDto? patchNews = null;
        if (PatchNewsReader.TryReadArticle(stackRoot, patch.Key, out var newsArticle, out _, out _))
        {
            patchNews = newsArticle;
        }

        string? launcherTheme = null;
        var hasLauncherTheme = false;
        var launcherConfigPath = Path.Combine(MigrationLayout.ConfigDir(stackRoot, patch.Key), PatchLauncherConfig.ConfigFileName);
        if (File.Exists(launcherConfigPath)
            && PatchLauncherConfig.TryReadTheme(launcherConfigPath, out var theme, out _))
        {
            hasLauncherTheme = true;
            launcherTheme = theme;
        }

        return new PatchDetailsDto
        {
            Key = patch.Key,
            Index = patch.Index.ToIndexString(),
            Level = patch.Level,
            Name = patch.DisplayName,
            Status = ResolveStatus(patch.Level, stack.AppliedPatchLevel, nextLevel),
            AppliedAt = appliedAt.TryGetValue(patch.Key, out var at) ? at : null,
            Description = MigrationLayout.ReadPatchDescription(stackRoot, patch.Key),
            DescriptionFile = MigrationLayout.FindPatchDescriptionFileName(stackRoot, patch.Key),
            Files = ListFiles(stackRoot, patch.Key),
            MpqRemovals = ReadMpqRemovals(stackRoot, patch.Key),
            Progression = metadata,
            ConfigOverrides = PatchConfigOverrideReader.ReadOverrides(stackRoot, patch.Key),
            HasPatchNews = patchNews is not null,
            PatchNewsTitle = patchNews?.Title,
            HasLauncherTheme = hasLauncherTheme,
            LauncherTheme = launcherTheme,
        };
    }

    public async Task<PatchNewsPreviewDto> GetPatchNewsPreviewAsync(
        string stackId,
        string patchKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var patch = RequirePatch(stackRoot, patchKey);

        if (!PatchNewsReader.TryReadArticle(stackRoot, patchKey, out var article, out var coverPath, out var error))
        {
            return new PatchNewsPreviewDto
            {
                Available = false,
                Error = error,
            };
        }

        var encodedKey = Uri.EscapeDataString(patchKey);
        var date = patch.Level <= stack.AppliedPatchLevel
            ? article.Date
            : PatchNewsWriter.TodayIsoDate();

        return new PatchNewsPreviewDto
        {
            Available = true,
            Id = article.Id,
            Title = article.Title,
            Date = date,
            Tag = article.Tag,
            Html = PatchNewsReader.RewriteHtmlForPreview(article.Html, stackId, patchKey),
            HasCover = coverPath is not null,
            CoverUrl = coverPath is not null
                ? $"/api/stacks/{stackId}/migrations/{encodedKey}/news-cover"
                : null,
            DateLocked = patch.Level <= stack.AppliedPatchLevel,
        };
    }

    public Task<(string Path, string ContentType)?> ResolvePatchNewsAssetAsync(
        string stackId,
        string patchKey,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stackRoot = GetStackRoot(stackId);
        RequirePatch(stackRoot, patchKey);

        var assetPath = PatchNewsReader.ResolveAssetPath(stackRoot, patchKey, relativePath);
        if (assetPath is null)
        {
            return Task.FromResult<(string Path, string ContentType)?>(null);
        }

        return Task.FromResult<(string Path, string ContentType)?>(
            (assetPath, GuessContentType(assetPath)));
    }

    public Task<(string Path, string ContentType)?> ResolvePatchNewsCoverAsync(
        string stackId,
        string patchKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stackRoot = GetStackRoot(stackId);
        RequirePatch(stackRoot, patchKey);

        var coverPath = PatchNewsReader.ResolveCoverImagePath(stackRoot, patchKey);
        if (coverPath is null)
        {
            return Task.FromResult<(string Path, string ContentType)?>(null);
        }

        return Task.FromResult<(string Path, string ContentType)?>(
            (coverPath, GuessContentType(coverPath)));
    }

    private static string GuessContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/png",
        };

    public async Task<List<PatchConfigOverrideDto>> GetPatchConfigOverridesPreviewAsync(
        string stackId,
        string patchKey,
        CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        RequirePatch(stackRoot, patchKey);

        var overrides = PatchConfigOverrideReader.ReadOverrides(stackRoot, patchKey);
        return await PatchConfigOverrideReader.EnrichWithCurrentValuesAsync(
            stackId,
            overrides,
            _serverConfig,
            cancellationToken);
    }

    public async Task<PatchDetailsDto> SavePatchDescriptionAsync(
        string stackId, string patchKey, string content, CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        RequirePatch(stackRoot, patchKey);
        MigrationLayout.SavePatchDescription(stackRoot, patchKey, content);
        return await GetPatchAsync(stackId, patchKey, cancellationToken);
    }

    public async Task<PatchDetailsDto> SavePatchNewsAsync(
        string stackId,
        string patchKey,
        SavePatchNewsRequest request,
        CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var patch = RequirePatch(stackRoot, patchKey);

        var date = ResolvePatchNewsDateForSave(stackRoot, patchKey, patch.Level, stack.AppliedPatchLevel);
        PatchNewsWriter.SaveArticle(stackRoot, patchKey, request, date);
        return await GetPatchAsync(stackId, patchKey, cancellationToken);
    }

    public async Task<PatchDetailsDto> UploadPatchNewsCoverAsync(
        string stackId,
        string patchKey,
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        RequirePatch(stackRoot, patchKey);
        PatchNewsWriter.SaveCover(stackRoot, patchKey, content, fileName);
        return await GetPatchAsync(stackId, patchKey, cancellationToken);
    }

    public async Task<PatchDetailsDto> SavePatchLauncherThemeAsync(
        string stackId,
        string patchKey,
        string theme,
        CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        RequirePatch(stackRoot, patchKey);

        if (!PatchLauncherConfig.TryNormalizeTheme(theme, out var normalized))
        {
            throw new ArgumentException("Theme must be one of: classic, tbc, wotlk.");
        }

        var configDir = MigrationLayout.ConfigDir(stackRoot, patchKey);
        Directory.CreateDirectory(configDir);
        var json = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["theme"] = normalized },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(
            Path.Combine(configDir, PatchLauncherConfig.ConfigFileName),
            json + Environment.NewLine,
            cancellationToken);

        return await GetPatchAsync(stackId, patchKey, cancellationToken);
    }

    public async Task<PatchSummaryDto> CreatePatchAsync(string stackId, CreatePatchRequest request, CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);

        var expansion = (request.Expansion ?? string.Empty).Trim().ToLowerInvariant();
        if (!MigrationLayout.ExpansionRoots.ContainsKey(expansion))
        {
            throw new ArgumentException("Expansion must be one of: classic, tbc, wotlk, custom.");
        }

        var displayName = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim();
        PatchIndex nextIndex;
        try
        {
            var tier = PatchIndex.ParseTier(request.Kind);
            nextIndex = ComputeNextPatchIndex(stackRoot, expansion, tier, request.ParentIndex);
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException(ex.Message);
        }
        var key = PatchFolderNames.Format(nextIndex, displayName);
        if (EnumeratePatches(stackRoot).Any(patch => patch.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Patch folder already exists: {key}");
        }

        MigrationLayout.EnsurePatchDirectories(stackRoot, key);

        return new PatchSummaryDto
        {
            Key = key,
            Index = nextIndex.ToIndexString(),
            Level = nextIndex.ToEncodedLevel(),
            Name = displayName ?? string.Empty,
            Status = PatchStatus.Locked,
            Description = MigrationLayout.ReadPatchDescription(stackRoot, key),
        };
    }

    public async Task<ImportPatchCollectionResultDto> ImportPatchCollectionAsync(
        string stackId,
        Stream zipContent,
        string mode,
        CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        if (IsApplyLockLive(stack))
        {
            throw new InvalidOperationException("Patches cannot be imported while an apply is in progress.");
        }

        var normalizedMode = NormalizeImportMode(mode);
        if (normalizedMode == "override" && HasAppliedPatches(stack))
        {
            throw new InvalidOperationException("Override is unavailable because one or more patches have already been applied. Use merge instead.");
        }

        var stackRoot = GetStackRoot(stackId);
        var tempArchive = Path.Combine(Path.GetTempPath(), "azp-patch-import-" + Guid.NewGuid().ToString("N") + ".archive");
        var tempExtract = Path.Combine(Path.GetTempPath(), "azp-patch-import-extract-" + Guid.NewGuid().ToString("N"));
        var tempStackRoot = Path.Combine(Path.GetTempPath(), "azp-patch-import-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using (var fs = File.Create(tempArchive))
            {
                await zipContent.CopyToAsync(fs, cancellationToken);
            }

            Directory.CreateDirectory(tempExtract);
            ArchiveExtractor.Extract(tempArchive, tempExtract, cancellationToken);
            Directory.CreateDirectory(tempStackRoot);

            var imported = await ExtractPatchCollectionAsync(
                tempExtract,
                tempStackRoot,
                stackRoot,
                normalizedMode,
                stack.AppliedPatchLevel,
                cancellationToken);

            var tempMigrationsRoot = MigrationLayout.MigrationsRoot(tempStackRoot);
            var migrationsRoot = MigrationLayout.MigrationsRoot(stackRoot);
            Directory.CreateDirectory(stackRoot);

            if (normalizedMode == "override")
            {
                if (Directory.Exists(migrationsRoot))
                {
                    Directory.Delete(migrationsRoot, recursive: true);
                }

                Directory.Move(tempMigrationsRoot, migrationsRoot);
            }
            else if (normalizedMode == "merge")
            {
                Directory.CreateDirectory(migrationsRoot);
                if (Directory.Exists(tempMigrationsRoot))
                {
                    foreach (var patchDir in Directory.EnumerateDirectories(tempMigrationsRoot))
                    {
                        var target = Path.Combine(migrationsRoot, Path.GetFileName(patchDir));
                        if (Directory.Exists(target))
                        {
                            throw new InvalidOperationException($"Patch already exists: {Path.GetFileName(patchDir)}");
                        }

                        Directory.Move(patchDir, target);
                    }
                }
            }
            else
            {
                Directory.CreateDirectory(migrationsRoot);
                foreach (var patchDir in Directory.EnumerateDirectories(tempMigrationsRoot))
                {
                    var target = Path.Combine(migrationsRoot, Path.GetFileName(patchDir));
                    if (Directory.Exists(target))
                    {
                        throw new InvalidOperationException($"Patch already exists: {Path.GetFileName(patchDir)}");
                    }

                    Directory.Move(patchDir, target);
                }
            }

            return new ImportPatchCollectionResultDto
            {
                Mode = normalizedMode,
                ImportedCount = imported.Count,
                ImportedPatches = imported
            };
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException(ex.Message);
        }
        catch (InvalidDataException)
        {
            throw new ArgumentException(
                "The uploaded file is not a supported archive. Use zip, rar, 7z, or tar (optionally gzip/bzip2/xz compressed).");
        }
        finally
        {
            try { File.Delete(tempArchive); } catch { /* best effort */ }
            try
            {
                if (Directory.Exists(tempExtract))
                {
                    Directory.Delete(tempExtract, recursive: true);
                }
            }
            catch
            {
                /* best effort */
            }
            try
            {
                if (Directory.Exists(tempStackRoot))
                {
                    Directory.Delete(tempStackRoot, recursive: true);
                }
            }
            catch
            {
                /* best effort */
            }
        }
    }

    private async Task<List<ImportedPatchDto>> ExtractPatchCollectionAsync(
        string extractedRoot,
        string tempStackRoot,
        string existingStackRoot,
        string mode,
        int appliedPatchLevel,
        CancellationToken cancellationToken)
    {
        var importableFiles = EnumerateImportableFiles(extractedRoot).ToList();
        var fileSegments = importableFiles.Select(file => file.Segments).ToList();
        var layout = ResolveImportPathLayout(fileSegments);

        var incoming = CollectIncomingPatches(fileSegments, layout);
        if (incoming.Count == 0)
        {
            throw new ArgumentException("The uploaded archive contained no patch folders.");
        }

        var existingPatches = mode is "append" or "merge"
            ? EnumeratePatches(existingStackRoot)
                .ToDictionary(patch => patch.Key, patch => patch, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PatchInfo>(StringComparer.OrdinalIgnoreCase);

        var existingKeys = existingPatches.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var patchMap = new Dictionary<(string Expansion, string SourceKey), string>();
        var patchDestinationRoots = new Dictionary<(string Expansion, string SourceKey), string>();
        var imported = new List<ImportedPatchDto>();
        var nextAppendSub1ByExpansion = mode == "append"
            ? ExpansionOrder.ToDictionary(
                expansion => expansion,
                expansion =>
                {
                    var root = MigrationLayout.ExpansionRoot(expansion);
                    var existingIndices = EnumeratePatches(existingStackRoot)
                        .Where(patch => patch.Index.ExpansionRoot == root)
                        .Select(patch => patch.Index)
                        .ToList();
                    return PatchIndex.ComputeNextAppendImportIndex(root, existingIndices).Sub1;
                },
                StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var expansion in ExpansionOrder)
        {
            var patches = incoming
                .Where(patch => patch.Expansion.Equals(expansion, StringComparison.OrdinalIgnoreCase))
                .OrderBy(patch => patch.Index);

            foreach (var patch in patches)
            {
                string targetKey;
                if (mode == "append")
                {
                    var root = MigrationLayout.ExpansionRoot(expansion);
                    PatchFolderNames.TryParse(patch.SourceKey, out _, out var displayName);
                    var sub1 = nextAppendSub1ByExpansion![expansion]++;
                    var newIndex = new PatchIndex(root, sub1);
                    targetKey = PatchFolderNames.Format(newIndex, displayName);
                }
                else
                {
                    patch.Index.AssertMatchesExpansion(expansion);
                    if (!PatchFolderNames.TryParse(patch.SourceKey, out _, out _))
                    {
                        throw new ArgumentException(
                            $"Patch folder '{patch.SourceKey}' must include an index (e.g. patch 1.1, patch 2 my_content).");
                    }

                    targetKey = patch.SourceKey;
                    if (mode == "merge")
                    {
                        targetKey = ResolveMergeTargetKey(patch.SourceKey, patch.Index, existingPatches);
                    }
                }

                if (existingKeys.Contains(targetKey))
                {
                    if (mode != "merge" || !existingPatches.ContainsKey(targetKey))
                    {
                        throw new InvalidOperationException($"Patch already exists: {targetKey}");
                    }

                    if (existingPatches[targetKey].Level <= appliedPatchLevel)
                    {
                        throw new InvalidOperationException($"Cannot merge content into already-applied patch: {targetKey}");
                    }
                }
                else
                {
                    existingKeys.Add(targetKey);
                }

                var destinationStackRoot = mode == "merge" && existingPatches.ContainsKey(targetKey)
                    ? existingStackRoot
                    : tempStackRoot;

                patchMap[(expansion, patch.SourceKey)] = targetKey;
                patchDestinationRoots[(expansion, patch.SourceKey)] = destinationStackRoot;
                MigrationLayout.EnsurePatchDirectories(destinationStackRoot, targetKey);

                imported.Add(new ImportedPatchDto
                {
                    Expansion = expansion,
                    SourceKey = patch.SourceKey,
                    TargetKey = targetKey
                });
            }
        }

        var copiedFiles = 0;
        foreach (var (sourcePath, segments) in importableFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryResolveImportPatchPath(segments, layout, out var expansion, out var sourceKey, out var categoryIndex))
            {
                continue;
            }

            if (!patchMap.TryGetValue((expansion, sourceKey), out var targetKey)
                || !patchDestinationRoots.TryGetValue((expansion, sourceKey), out var destinationStackRoot))
            {
                continue;
            }

            if (IsImportPatchDescriptionPath(segments, layout, categoryIndex)
                && MigrationLayout.IsPatchDescriptionFile(segments[^1]))
            {
                var descriptionPath = Path.Combine(MigrationLayout.PatchDir(destinationStackRoot, targetKey), segments[^1]);
                Directory.CreateDirectory(Path.GetDirectoryName(descriptionPath)!);
                File.Copy(sourcePath, descriptionPath, overwrite: true);

                copiedFiles++;
                continue;
            }

            var (category, relativePath, skip) = ParseImportedCategoryPath(segments, categoryIndex);
            if (skip)
            {
                continue;
            }

            if (category.Equals("mpq", StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(relativePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var json = await File.ReadAllTextAsync(sourcePath, cancellationToken);
                if (TryParseMpqRemovalJson(json, out var removals))
                {
                    AppendMpqRemovals(destinationStackRoot, targetKey, removals);
                    copiedFiles++;
                    continue;
                }

                // Ignore other JSON files in mpq/ that are not valid removal instructions.
                continue;
            }

            var (destination, normalizedCategory, _) = ResolveCategoryFile(destinationStackRoot, targetKey, category, relativePath);
            if (normalizedCategory.Equals("mpq", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(destination).Equals(_migrationOptions.PatchDMpqName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "patch-D.MPQ is reserved for generated DBC content and cannot be imported as an MPQ patch file.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourcePath, destination, overwrite: true);

            var ext = Path.GetExtension(destination).ToLowerInvariant();
            if (ext is ".csv" or ".txt")
            {
                await EnsureTrailingNewlineAsync(destination, cancellationToken);
            }

            if (normalizedCategory.Equals("mpq", StringComparison.OrdinalIgnoreCase)
                && !File.Exists(MpqDescriptionPath(destination)))
            {
                await File.WriteAllTextAsync(
                    MpqDescriptionPath(destination),
                    "Imported from patch collection.",
                    cancellationToken);
            }

            copiedFiles++;
        }

        if (copiedFiles == 0)
        {
            throw new ArgumentException("The uploaded archive contained no importable patch files.");
        }

        return imported;
    }

    private static IEnumerable<(string SourcePath, string[] Segments)> EnumerateImportableFiles(string extractedRoot)
    {
        foreach (var file in Directory.EnumerateFiles(extractedRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(extractedRoot, file);
            var segments = NormalizeZipPath(relative);
            if (IsIgnoredArchiveEntry(segments))
            {
                continue;
            }

            yield return (file, segments);
        }
    }

    private sealed record ImportPathLayout(bool UsesExpansionFolders, int ExpansionSegmentIndex);

    private static ImportPathLayout ResolveImportPathLayout(IReadOnlyList<string[]> fileSegments)
    {
        var expansionOffset = FindExpansionOffset(fileSegments);
        if (expansionOffset.HasValue)
        {
            return new ImportPathLayout(true, expansionOffset.Value);
        }

        if (fileSegments.Any(segments =>
                segments.Length >= 2 && PatchFolderNames.TryParse(segments[0], out _, out _)))
        {
            return new ImportPathLayout(false, 0);
        }

        throw new ArgumentException(
            "The archive must contain classic/tbc/wotlk folders, or patch folders named 'patch {index}' at the root.");
    }

    private static bool TryResolveImportPatchPath(
        string[] segments,
        ImportPathLayout layout,
        out string expansion,
        out string sourceKey,
        out int categoryIndex)
    {
        expansion = string.Empty;
        sourceKey = string.Empty;
        categoryIndex = 0;

        if (layout.UsesExpansionFolders)
        {
            if (segments.Length <= layout.ExpansionSegmentIndex + 2)
            {
                return false;
            }

            expansion = segments[layout.ExpansionSegmentIndex].ToLowerInvariant();
            if (!MigrationLayout.ExpansionRoots.ContainsKey(expansion))
            {
                return false;
            }

            sourceKey = segments[layout.ExpansionSegmentIndex + 1];
            categoryIndex = layout.ExpansionSegmentIndex + 2;
            return true;
        }

        if (segments.Length < 2 || !PatchFolderNames.TryParse(segments[0], out var index, out _))
        {
            return false;
        }

        sourceKey = segments[0];
        expansion = MigrationLayout.ExpansionName(index.ExpansionRoot);
        categoryIndex = 1;
        return true;
    }

    private static bool IsImportPatchDescriptionPath(string[] segments, ImportPathLayout layout, int categoryIndex) =>
        layout.UsesExpansionFolders
            ? segments.Length == layout.ExpansionSegmentIndex + 3
            : segments.Length == 2 && categoryIndex == 1;

    private static IReadOnlyList<IncomingPatch> CollectIncomingPatches(
        IReadOnlyList<string[]> fileSegments,
        ImportPathLayout layout)
    {
        var patches = new Dictionary<(string Expansion, string SourceKey), IncomingPatch>();
        foreach (var segments in fileSegments)
        {
            if (!TryResolveImportPatchPath(segments, layout, out var expansion, out var sourceKey, out _))
            {
                continue;
            }

            if (!PatchFolderNames.TryParse(sourceKey, out var index, out _))
            {
                throw new ArgumentException(
                    $"Patch folder '{sourceKey}' must be named 'patch {{index}}' or 'patch {{index}} {{name}}' (e.g. patch 1, patch 1.1, patch 2 my_content).");
            }

            index.AssertMatchesExpansion(expansion);

            var key = (expansion, sourceKey);
            if (!patches.ContainsKey(key))
            {
                patches[key] = new IncomingPatch(expansion, sourceKey, index);
            }
        }

        return patches.Values.ToList();
    }

    private static int? FindExpansionOffset(IEnumerable<string[]> fileSegments)
    {
        var offsetCounts = new Dictionary<int, int>();
        foreach (var segments in fileSegments)
        {
            if (IsIgnoredArchiveEntry(segments))
            {
                continue;
            }

            for (var i = 0; i <= segments.Length - 3; i++)
            {
                if (!MigrationLayout.ExpansionRoots.ContainsKey(segments[i]))
                {
                    continue;
                }

                if (!PatchFolderNames.TryParse(segments[i + 1], out _, out _))
                {
                    continue;
                }

                offsetCounts.TryGetValue(i, out var count);
                offsetCounts[i] = count + 1;
            }
        }

        if (offsetCounts.Count == 0)
        {
            return null;
        }

        return offsetCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .First()
            .Key;
    }

    /// <summary>
    /// In merge mode, map archive folder names onto existing library folders by patch index when
    /// the full folder name differs (e.g. preset <c>patch 1.0</c> → template <c>patch 1.0 START</c>).
    /// </summary>
    private static string ResolveMergeTargetKey(
        string sourceKey,
        PatchIndex sourceIndex,
        IReadOnlyDictionary<string, PatchInfo> existingPatches)
    {
        if (existingPatches.ContainsKey(sourceKey))
        {
            return sourceKey;
        }

        var indexMatches = existingPatches.Values
            .Where(patch => patch.Index.Equals(sourceIndex))
            .Select(patch => patch.Key)
            .ToList();

        return indexMatches.Count == 1 ? indexMatches[0] : sourceKey;
    }

    private static (string Category, string RelativePath, bool Skip) ParseImportedCategoryPath(string[] segments, int categoryIndex)
    {
        if (segments.Length <= categoryIndex)
        {
            return (string.Empty, string.Empty, true);
        }

        var head = segments[categoryIndex].ToLowerInvariant();
        string category;
        int fileStart;

        if (head == "sql")
        {
            if (segments.Length <= categoryIndex + 1)
            {
                return (string.Empty, string.Empty, true);
            }

            var database = segments[categoryIndex + 1].ToLowerInvariant();
            if (!MigrationLayout.SqlDatabases.ContainsKey(database))
            {
                throw new ArgumentException($"Unknown SQL database folder: {segments[categoryIndex + 1]}");
            }

            category = $"sql/{database}";
            fileStart = categoryIndex + 2;
        }
        else if (MigrationLayout.SqlDatabases.ContainsKey(head))
        {
            category = $"sql/{head}";
            fileStart = categoryIndex + 1;
        }
        else
        {
            category = head switch
            {
                "dbc" => "dbc",
                "map" or "maps" => "map",
                "mpq" => "mpq",
                "lua" => "lua",
                _ => throw new ArgumentException($"Unknown patch content folder: {segments[categoryIndex]}")
            };
            fileStart = categoryIndex + 1;
        }

        if (segments.Length <= fileStart)
        {
            return (string.Empty, string.Empty, true);
        }

        var relativePath = string.Join('/', segments.Skip(fileStart));
        if (category == "mpq" && relativePath.EndsWith(".desc", StringComparison.OrdinalIgnoreCase))
        {
            return (category, relativePath, true);
        }

        return (category, relativePath, false);
    }

    private static string NormalizeImportMode(string mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "append" => "append",
            "override" => "override",
            "merge" or "" => "merge",
            _ => throw new ArgumentException("Import mode must be append, override, or merge.")
        };
    }

    private static bool HasAppliedPatches(ManagedStackEntity stack) =>
        stack.AppliedPatchLevel > 0 || ParseAppliedPatches(stack.AppliedPatchesJson).Count > 0;

    private static string FormatCurrentIndex(int encodedLevel) =>
        encodedLevel > 0 ? PatchIndex.FromEncodedLevel(encodedLevel).ToIndexString() : string.Empty;

    private static PatchIndex ComputeNextPatchIndex(
        string stackRoot,
        string expansion,
        PatchTier tier,
        string? parentIndex = null)
    {
        var root = MigrationLayout.ExpansionRoot(expansion);
        var existing = EnumeratePatches(stackRoot)
            .Select(patch => patch.Index)
            .ToList();

        return PatchIndex.ComputeNext(tier, root, existing, parentIndex);
    }

    private static string[] NormalizeZipPath(string path)
    {
        var segments = path
            .Replace('\\', '/')
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException($"Invalid zip entry path: {path}");
        }

        return segments;
    }

    private static bool IsIgnoredArchiveEntry(string[] segments)
    {
        if (segments.Length == 0)
        {
            return true;
        }

        var fileName = segments[^1];
        return segments.Any(segment => segment.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))
               || fileName.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
               || fileName.StartsWith("._", StringComparison.Ordinal);
    }

    public async Task<ClientBrowseResultDto> BrowsePatchFilesAsync(
        string stackId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var migrationsRoot = MigrationLayout.MigrationsRoot(stackRoot);
        var normalized = NormalizeBrowsePath(relativePath);
        var result = new ClientBrowseResultDto { Path = normalized };

        var target = ResolveWithinMigrations(migrationsRoot, normalized);
        if (target is null || !Directory.Exists(target))
        {
            return result;
        }

        result.Exists = true;

        foreach (var dir in Directory.EnumerateDirectories(target))
        {
            var name = Path.GetFileName(dir);
            var entryPath = CombineRelative(normalized, name);
            var childCount = 0;
            try
            {
                childCount = Directory.EnumerateFileSystemEntries(dir).Count();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to count children of {Dir}", dir);
            }

            result.Entries.Add(new ClientBrowseEntryDto
            {
                Name = name,
                IsDirectory = true,
                ItemCount = childCount,
                RelativePath = entryPath,
                IsLocked = IsAppliedPatchPath(stackRoot, stack.AppliedPatchLevel, entryPath)
            });
        }

        foreach (var file in Directory.EnumerateFiles(target))
        {
            var name = Path.GetFileName(file);
            long size = 0;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to stat {File}", file);
            }

            var entryPath = CombineRelative(normalized, name);
            result.Entries.Add(new ClientBrowseEntryDto
            {
                Name = name,
                IsDirectory = false,
                Size = size,
                RelativePath = entryPath,
                IsLocked = IsAppliedPatchPath(stackRoot, stack.AppliedPatchLevel, entryPath)
            });
        }

        result.Entries.Sort(CompareBrowseEntries);

        return result;
    }

    public async Task DeletePatchEntryAsync(
        string stackId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        if (IsApplyLockLive(stack))
        {
            throw new InvalidOperationException("Patch files cannot be deleted while an apply is in progress.");
        }

        var stackRoot = GetStackRoot(stackId);
        var migrationsRoot = MigrationLayout.MigrationsRoot(stackRoot);
        var normalized = NormalizeBrowsePath(relativePath);
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("Refusing to delete the patches root. Delete individual patch files or folders instead.");
        }

        if (stack.AppliedPatchLevel > 0)
        {
            if (IsAppliedPatchPath(stackRoot, stack.AppliedPatchLevel, normalized))
            {
                throw new InvalidOperationException("This patch has already been applied and its files are locked.");
            }

            // Require the first path segment to be a real, unapplied patch folder. This avoids deleting
            // unrelated migration metadata if it ever appears beside patch folders.
            var patchKey = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            var patch = RequirePatch(stackRoot, patchKey);
            if (patch.Level <= stack.AppliedPatchLevel)
            {
                throw new InvalidOperationException("This patch has already been applied and its files are locked.");
            }
        }
        else
        {
            // Nothing applied yet — still require the first segment to be a known patch folder.
            var patchKey = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            RequirePatch(stackRoot, patchKey);
        }

        var target = ResolveWithinMigrations(migrationsRoot, normalized)
            ?? throw new InvalidOperationException("Invalid path.");

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
        else if (File.Exists(target))
        {
            File.Delete(target);
            if (Path.GetExtension(target).Equals(".mpq", StringComparison.OrdinalIgnoreCase))
            {
                var descPath = MpqDescriptionPath(target);
                if (File.Exists(descPath))
                {
                    File.Delete(descPath);
                }
            }
        }
        else
        {
            throw new InvalidOperationException("The file or folder no longer exists.");
        }

        _logger.LogInformation("Deleted patch entry '{Path}' for stack {StackId}.", normalized, stackId);
    }

    public async Task<int> DeleteAllPatchesAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        if (IsApplyLockLive(stack))
        {
            throw new InvalidOperationException("Patches cannot be deleted while an apply is in progress.");
        }

        if (HasAppliedPatches(stack))
        {
            throw new InvalidOperationException(
                "Cannot drop all patches after any patch has been applied.");
        }

        var migrationsRoot = MigrationLayout.MigrationsRoot(GetStackRoot(stackId));
        if (!Directory.Exists(migrationsRoot))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var patchDir in Directory.EnumerateDirectories(migrationsRoot).ToList())
        {
            Directory.Delete(patchDir, recursive: true);
            deleted++;
        }

        _logger.LogInformation("Dropped {Count} patch folder(s) for stack {StackId}.", deleted, stackId);
        return deleted;
    }

    private static bool IsAppliedPatchPath(string stackRoot, int appliedPatchLevel, string relativePath)
    {
        if (appliedPatchLevel <= 0)
        {
            return false;
        }

        var segments = NormalizeBrowsePath(relativePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var patch = EnumeratePatches(stackRoot).FirstOrDefault(p =>
            p.Key.Equals(segments[0], StringComparison.OrdinalIgnoreCase));
        return patch is not null && patch.Level <= appliedPatchLevel;
    }

    private static string NormalizeBrowsePath(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Invalid path.");
        }

        return string.Join('/', segments);
    }

    private static string? ResolveWithinMigrations(string migrationsRoot, string relativePath)
    {
        var rootFull = Path.GetFullPath(migrationsRoot);
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        var normalized = NormalizeBrowsePath(relativePath);
        var target = Path.GetFullPath(Path.Combine(rootFull, normalized));
        if (!target.Equals(rootFull, StringComparison.Ordinal) && !target.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            return null;
        }

        return target;
    }

    private static string CombineRelative(string basePath, string name) =>
        string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";

    private static int CompareBrowseEntries(ClientBrowseEntryDto a, ClientBrowseEntryDto b)
    {
        if (a.IsDirectory != b.IsDirectory)
        {
            return a.IsDirectory ? -1 : 1;
        }

        if (a.IsDirectory && b.IsDirectory
            && PatchFolderNames.TryParse(a.Name, out var aIndex, out _)
            && PatchFolderNames.TryParse(b.Name, out var bIndex, out _))
        {
            var indexCompare = aIndex.CompareTo(bIndex);
            if (indexCompare != 0)
            {
                return indexCompare;
            }
        }

        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    // ===== File CRUD =====

    public async Task<PatchFileDto> UploadFileAsync(
        string stackId, string patchKey, string category, string fileName, Stream content, string? description = null, CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        RequirePatch(stackRoot, patchKey);

        // The "file name" may include a single container sub-folder for container categories,
        // e.g. "gems/Item.csv" (dbc) or "kalimdor/1234.map" (map).
        var (destination, normalizedCategory, displayName) = ResolveCategoryFile(stackRoot, patchKey, category, fileName);

        var isMpq = string.Equals(normalizedCategory, "mpq", StringComparison.OrdinalIgnoreCase);

        // patch-D is a reserved slot: it is generated automatically from the compiled DBC files, so a
        // manual upload of it would be silently overwritten on the next apply. Reject it up front.
        if (isMpq
            && string.Equals(Path.GetFileName(destination), _migrationOptions.PatchDMpqName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "patch-D.MPQ is reserved for DBC files and is compiled automatically from the CSV files placed in the DBC section. Upload your DBC changes as CSV/.txt files there instead.");
        }

        // MPQ archives can be large opaque blobs, so require the uploader to describe their contents.
        if (isMpq && string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("A description of the MPQ's contents is required.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await using (var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(fileStream, cancellationToken);
        }

        // CSV/.txt uploads (DBC sources) must end with a newline: WDBXEditor's importer corrupts the
        // final row of a file with no trailing newline. Validate and fix at upload time.
        var ext = Path.GetExtension(destination).ToLowerInvariant();
        if (ext is ".csv" or ".txt")
        {
            await EnsureTrailingNewlineAsync(destination, cancellationToken);
        }

        string? storedDescription = null;
        if (isMpq)
        {
            storedDescription = description!.Trim();
            await File.WriteAllTextAsync(MpqDescriptionPath(destination), storedDescription, cancellationToken);
        }

        var info = new FileInfo(destination);
        return new PatchFileDto
        {
            Category = normalizedCategory,
            Name = displayName,
            Size = info.Length,
            Description = storedDescription
        };
    }

    /// <summary>
    /// Ensures a text file ends with a newline. WDBXEditor's CSV reader strips the last line of a file
    /// with no trailing newline, corrupting the final row; guaranteeing one avoids that.
    /// </summary>
    private static async Task EnsureTrailingNewlineAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length == 0)
        {
            return;
        }

        byte last;
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            fs.Seek(-1, SeekOrigin.End);
            last = (byte)fs.ReadByte();
        }

        if (last != (byte)'\n')
        {
            await File.AppendAllTextAsync(path, "\r\n", cancellationToken);
        }
    }

    /// <summary>Sidecar path holding an MPQ's description (e.g. "foo.mpq" -> "foo.mpq.desc").</summary>
    private static string MpqDescriptionPath(string mpqPath) => mpqPath + ".desc";

    /// <summary>Reads an MPQ's stored description sidecar, or null if none exists.</summary>
    private static string? ReadMpqDescription(string mpqPath)
    {
        var descPath = MpqDescriptionPath(mpqPath);
        if (!File.Exists(descPath))
        {
            return null;
        }

        var text = File.ReadAllText(descPath).Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public async Task<string> ReadDbcFileAsync(string stackId, string patchKey, string fileName, CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        RequirePatch(stackRoot, patchKey);

        var (path, _, _) = ResolveCategoryFile(stackRoot, patchKey, "dbc", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"DBC file not found: {fileName}");
        }

        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    public async Task SaveDbcFileAsync(string stackId, string patchKey, string fileName, string content, CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        RequirePatch(stackRoot, patchKey);

        var (path, _, _) = ResolveCategoryFile(stackRoot, patchKey, "dbc", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    public async Task DeleteFileAsync(string stackId, string patchKey, string category, string fileName, CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        RequirePatch(stackRoot, patchKey);

        var (path, normalizedCategory, _) = ResolveCategoryFile(stackRoot, patchKey, category, fileName);
        if (!File.Exists(path))
        {
            return;
        }

        File.Delete(path);

        // Remove the MPQ description sidecar alongside the archive.
        if (string.Equals(normalizedCategory, "mpq", StringComparison.OrdinalIgnoreCase))
        {
            var descPath = MpqDescriptionPath(path);
            if (File.Exists(descPath))
            {
                File.Delete(descPath);
            }
        }

        // Remove an emptied container sub-folder so it stops showing in the UI.
        if (AllowsContainers(normalizedCategory))
        {
            var parent = Path.GetDirectoryName(path);
            var baseDir = ResolveCategoryDir(stackRoot, patchKey, category);
            if (parent is not null
                && !string.Equals(Path.GetFullPath(parent), Path.GetFullPath(baseDir), StringComparison.Ordinal)
                && Directory.Exists(parent)
                && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
    }

    // ===== MPQ removals =====

    /// <summary>File (inside a patch's mpq folder) listing published MPQs the patch removes on apply.</summary>
    internal const string MpqRemovalsFileName = ".remove.json";

    private static string MpqRemovalsPath(string stackRoot, string patchKey) =>
        Path.Combine(MigrationLayout.MpqDir(stackRoot, patchKey), MpqRemovalsFileName);

    /// <summary>Reads a patch's MPQ removal list (base file names), or an empty list if none/invalid.</summary>
    internal static List<string> ReadMpqRemovals(string stackRoot, string patchKey)
    {
        var path = MpqRemovalsPath(stackRoot, patchKey);
        if (!File.Exists(path))
        {
            return new List<string>();
        }

        return TryParseMpqRemovalJson(File.ReadAllText(path), out var removals)
            ? removals
            : new List<string>();
    }

    /// <summary>Reads and parses an mpq.json manifest from a patch's mpq directory, or null if none exists.</summary>
    internal static MpqManifestDto? ReadMpqManifest(string stackRoot, string patchKey)
    {
        var path = Path.Combine(MigrationLayout.MpqDir(stackRoot, patchKey), "mpq.json");
        if (!File.Exists(path))
            return null;

        return MpqManifestReader.Parse(File.ReadAllText(path));
    }

    /// <summary>Merges legacy remove.json entries with <c>remove</c> names from mpq.json.</summary>
    internal static List<string> CollectMpqRemovals(string stackRoot, string patchKey)
    {
        var removals = ReadMpqRemovals(stackRoot, patchKey);
        var manifest = ReadMpqManifest(stackRoot, patchKey);
        if (manifest is null || manifest.Remove.Count == 0)
        {
            return removals;
        }

        return removals.Concat(manifest.Remove)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ResolveMpqFileDescription(string stackRoot, string patchKey, string mpqFilePath)
    {
        var sidecar = ReadMpqDescription(mpqFilePath);
        if (!string.IsNullOrWhiteSpace(sidecar))
        {
            return sidecar;
        }

        var fileName = Path.GetFileName(mpqFilePath);
        var manifest = ReadMpqManifest(stackRoot, patchKey);
        if (manifest?.Description.TryGetValue(fileName, out var desc) == true
            && !string.IsNullOrWhiteSpace(desc))
        {
            return desc.Trim();
        }

        return null;
    }

    /// <summary>
    /// Resolves the final set of MPQ files to construct across all applied patches.
    /// MPQs that are added by an earlier patch but removed by a later one are skipped.
    /// </summary>
    private static MpqConstructionPlanDto ResolveMpqConstructionPlan(string stackRoot, IReadOnlyList<PatchInfo> appliedPatches)
    {
        var adds = new List<(string MpqName, string PatchKey, string? Description)>();
        var removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var patch in appliedPatches.OrderBy(p => p.Level))
        {
            var manifest = ReadMpqManifest(stackRoot, patch.Key);
            if (manifest is null)
                continue;

            foreach (var name in manifest.Remove)
            {
                removals.Add(name);
            }

            foreach (var name in manifest.Add)
            {
                if (!MpqPackFilter.IsValidConstructedMpqName(name))
                {
                    continue;
                }

                manifest.Description.TryGetValue(name, out var desc);
                adds.Add((name, patch.Key, desc));
            }
        }

        foreach (var patch in appliedPatches.OrderBy(p => p.Level))
        {
            foreach (var removal in ReadMpqRemovals(stackRoot, patch.Key))
            {
                removals.Add(removal);
            }
        }

        var plan = new MpqConstructionPlanDto();

        var lastAddByName = new Dictionary<string, (string PatchKey, string? Description)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, patchKey, desc) in adds)
        {
            lastAddByName[name] = (patchKey, desc);
        }

        foreach (var (name, (patchKey, desc)) in lastAddByName)
        {
            if (removals.Contains(name))
            {
                var addPatch = appliedPatches.First(p => p.Key.Equals(patchKey, StringComparison.OrdinalIgnoreCase));
                var removedByLater = appliedPatches
                    .Where(p => p.Level > addPatch.Level)
                    .Any(p =>
                    {
                        var m = ReadMpqManifest(stackRoot, p.Key);
                        var r = ReadMpqRemovals(stackRoot, p.Key);
                        return (m?.Remove.Contains(name, StringComparer.OrdinalIgnoreCase) ?? false)
                            || r.Contains(name, StringComparer.OrdinalIgnoreCase);
                    });

                if (removedByLater)
                {
                    plan.Skipped.Add(name);
                    continue;
                }
            }

            var mpqPath = Path.Combine(MigrationLayout.MpqDir(stackRoot, patchKey), name);
            plan.ToBuild.Add(new MpqConstructionEntryDto
            {
                MpqName = name,
                PatchKey = patchKey,
                Description = desc,
                PreBuilt = File.Exists(mpqPath)
            });
        }

        return plan;
    }

    /// <summary>
    /// Parses MPQ removal instructions from JSON. Supports a string array, or an object with a
    /// case-insensitive <c>remove</c> property holding one file name or an array of names.
    /// </summary>
    internal static bool TryParseMpqRemovalJson(string json, out List<string> removals)
    {
        removals = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                var names = root.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty);
                removals = NormalizeMpqRemovals(names);
                return removals.Count > 0;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (!property.Name.Equals("remove", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    removals = NormalizeMpqRemovals(ExtractMpqRemovalNames(property.Value));
                    return removals.Count > 0;
                }
            }
        }
        catch (JsonException)
        {
            // Not a removal instruction document.
        }

        removals = new List<string>();
        return false;
    }

    private static IEnumerable<string> ExtractMpqRemovalNames(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => new[] { value.GetString() ?? string.Empty },
            JsonValueKind.Array => value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty),
            _ => Enumerable.Empty<string>()
        };

    private static void AppendMpqRemovals(string stackRoot, string patchKey, IEnumerable<string> additional)
    {
        var merged = NormalizeMpqRemovals(ReadMpqRemovals(stackRoot, patchKey).Concat(additional));
        WriteMpqRemovals(stackRoot, patchKey, merged);
    }

    private static void WriteMpqRemovals(string stackRoot, string patchKey, IReadOnlyList<string> names)
    {
        var path = MpqRemovalsPath(stackRoot, patchKey);
        if (names.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        Directory.CreateDirectory(MigrationLayout.MpqDir(stackRoot, patchKey));
        File.WriteAllText(path, JsonSerializer.Serialize(names, JsonOptions));
    }

    /// <summary>Sanitizes removal entries to distinct base ".mpq" file names (drops any path/traversal).</summary>
    private static List<string> NormalizeMpqRemovals(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var name = Path.GetFileName(raw.Replace('\\', '/').Trim());
            if (string.IsNullOrEmpty(name)
                || !Path.GetExtension(name).Equals(".mpq", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    public async Task<List<PublishedMpqDto>> GetPublishedMpqsAsync(string stackId, CancellationToken cancellationToken = default)
    {
        var stack = await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var overlayDataDir = MigrationLayout.ClientOverlayDataDir(stackRoot);
        var patchDName = _migrationOptions.PatchDMpqName;

        // Only surface MPQs that belong to a successfully-applied patch (level <= current applied
        // level). This deliberately ignores archives a *failed* apply may have copied into the overlay
        // before erroring out mid-run: they aren't "published" until that patch actually applies.
        var appliedMpqNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var patch in EnumeratePatches(stackRoot).Where(p => p.Level <= stack.AppliedPatchLevel))
        {
            foreach (var mpq in EnumerateMpqFiles(MigrationLayout.MpqDir(stackRoot, patch.Key)))
            {
                appliedMpqNames.Add(Path.GetFileName(mpq));
            }
        }

        return EnumerateMpqFiles(overlayDataDir)
            .Select(path => new FileInfo(path))
            // patch-D.MPQ is generated from applied DBCs (reserved), so it never appears in a patch's
            // mpq folder — include it on presence. Everything else must be backed by an applied patch.
            .Where(info => info.Name.Equals(patchDName, StringComparison.OrdinalIgnoreCase)
                || appliedMpqNames.Contains(info.Name))
            .Select(info => new PublishedMpqDto
            {
                Name = info.Name,
                Size = info.Length,
                IsReserved = info.Name.Equals(patchDName, StringComparison.OrdinalIgnoreCase)
            })
            .OrderBy(dto => dto.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task SetMpqRemovalsAsync(string stackId, string patchKey, IReadOnlyList<string> fileNames, CancellationToken cancellationToken = default)
    {
        await GetStackAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        RequirePatch(stackRoot, patchKey);

        var normalized = NormalizeMpqRemovals(fileNames ?? Array.Empty<string>());
        WriteMpqRemovals(stackRoot, patchKey, normalized);
    }

    // ===== Helpers: enumeration =====

    /// <summary>Categories that support one level of organizational "container" sub-folders.</summary>
    private static readonly string[] ContainerCategories =
        { "dbc", "map", "lua", "sql/world", "sql/auth", "sql/characters" };

    private static bool AllowsContainers(string normalizedCategory) =>
        ContainerCategories.Contains(normalizedCategory, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a file name is accepted in a category (per-category extension rules).</summary>
    private static bool CategoryAccepts(string normalizedCategory, string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return normalizedCategory switch
        {
            // CSV/.txt are compiled onto the server baseline; .dbc are uploaded directly (no compile).
            "dbc" => ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                     || ext.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                     || ext.Equals(".dbc", StringComparison.OrdinalIgnoreCase),
            "mpq" => ext.Equals(".mpq", StringComparison.OrdinalIgnoreCase),
            "config" => ext.Equals(".json", StringComparison.OrdinalIgnoreCase),
            "lua" => ext.Equals(".lua", StringComparison.OrdinalIgnoreCase)
                     || ext.Equals(".ext", StringComparison.OrdinalIgnoreCase),
            "sql/world" or "sql/auth" or "sql/characters" => ext.Equals(".sql", StringComparison.OrdinalIgnoreCase),
            "map" => true,
            _ => false
        };
    }

    /// <summary>
    /// Enumerates a category's files: at the directory root plus (for container categories) inside
    /// exactly one level of sub-folders. Deeper nesting is ignored (uploads reject it), so two
    /// same-named files can live in different containers without colliding on disk.
    /// </summary>
    private static IEnumerable<string> EnumerateCategoryFiles(string dir, string normalizedCategory)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                     .Where(p => CategoryAccepts(normalizedCategory, p)))
        {
            yield return file;
        }

        if (!AllowsContainers(normalizedCategory))
        {
            yield break;
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            foreach (var file in Directory.EnumerateFiles(sub, "*", SearchOption.TopDirectoryOnly)
                         .Where(p => CategoryAccepts(normalizedCategory, p)))
            {
                yield return file;
            }
        }
    }

    /// <summary>All DBC-category files (CSV/.txt sources and direct .dbc; root + one container level).</summary>
    internal static IEnumerable<string> EnumerateDbcSourceFiles(string dir) =>
        EnumerateCategoryFiles(dir, "dbc");

    /// <summary>True for a CSV/.txt DBC source that must be compiled onto the baseline via WDBXEditor.</summary>
    internal static bool IsDbcCsvSource(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".csv", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a pre-built .dbc uploaded directly (no server export / CSV compile needed).</summary>
    internal static bool IsDbcBinary(string path) =>
        Path.GetExtension(path).Equals(".dbc", StringComparison.OrdinalIgnoreCase);

    private sealed record PatchInfo(string Key, int Level, PatchIndex Index, string DisplayName);

    private sealed record IncomingPatch(string Expansion, string SourceKey, PatchIndex Index);

    private static IReadOnlyList<PatchInfo> EnumeratePatches(string stackRoot)
    {
        var migrationsRoot = MigrationLayout.MigrationsRoot(stackRoot);
        if (!Directory.Exists(migrationsRoot))
        {
            return Array.Empty<PatchInfo>();
        }

        var results = new List<PatchInfo>();
        foreach (var dir in Directory.EnumerateDirectories(migrationsRoot))
        {
            var key = Path.GetFileName(dir);
            if (!PatchFolderNames.TryParse(key, out var index, out var displayName))
            {
                continue;
            }

            results.Add(new PatchInfo(key, index.ToEncodedLevel(), index, displayName ?? string.Empty));
        }

        return results.OrderBy(patch => patch.Index).ToList();
    }

    private static PatchInfo RequirePatch(string stackRoot, string patchKey)
    {
        var patch = EnumeratePatches(stackRoot).FirstOrDefault(p =>
            string.Equals(p.Key, patchKey, StringComparison.OrdinalIgnoreCase));

        return patch ?? throw new FileNotFoundException($"Patch not found: {patchKey}");
    }

    private static PatchStatus ResolveStatus(int level, int currentLevel, int? nextLevel)
    {
        if (level <= currentLevel)
        {
            return PatchStatus.Applied;
        }

        return nextLevel.HasValue && level == nextLevel.Value ? PatchStatus.Next : PatchStatus.Locked;
    }

    private static string ResolvePatchNewsDateForSave(
        string stackRoot,
        string patchKey,
        int patchLevel,
        int appliedPatchLevel)
    {
        if (patchLevel <= appliedPatchLevel
            && PatchNewsReader.TryReadArticle(stackRoot, patchKey, out var existing, out _, out _)
            && !string.IsNullOrWhiteSpace(existing.Date))
        {
            return existing.Date.Trim();
        }

        return PatchNewsWriter.TodayIsoDate();
    }

    private static (int Sql, int Dbc, int Map, int Mpq) CountFiles(string stackRoot, string patchKey)
    {
        var sql = MigrationLayout.SqlDatabases.Keys.Sum(database =>
            EnumerateCategoryFiles(MigrationLayout.SqlDatabaseDir(stackRoot, patchKey, database), $"sql/{database}").Count());
        var dbc = EnumerateCategoryFiles(MigrationLayout.DbcDir(stackRoot, patchKey), "dbc").Count();
        var map = EnumerateCategoryFiles(MigrationLayout.MapDir(stackRoot, patchKey), "map").Count();
        var mpq = EnumerateCategoryFiles(MigrationLayout.MpqDir(stackRoot, patchKey), "mpq").Count();
        return (sql, dbc, map, mpq);
    }

    private static List<PatchFileDto> ListFiles(string stackRoot, string patchKey)
    {
        var files = new List<PatchFileDto>();

        foreach (var database in MigrationLayout.SqlDatabases.Keys)
        {
            AddCategoryFiles(files, MigrationLayout.SqlDatabaseDir(stackRoot, patchKey, database), $"sql/{database}");
        }

        AddCategoryFiles(files, MigrationLayout.DbcDir(stackRoot, patchKey), "dbc");
        AddCategoryFiles(files, MigrationLayout.MapDir(stackRoot, patchKey), "map");
        AddCategoryFiles(files, MigrationLayout.MpqDir(stackRoot, patchKey), "mpq", stackRoot, patchKey);
        AddCategoryFiles(files, MigrationLayout.ConfigDir(stackRoot, patchKey), "config");
        AddLuaCategoryFiles(files, MigrationLayout.PatchLuaDir(stackRoot, patchKey));
        AddNewsCategoryFiles(files, MigrationLayout.PatchNewsDir(stackRoot, patchKey));

        return files;
    }

    private static void AddNewsCategoryFiles(List<PatchFileDto> target, string newsDir)
    {
        if (!Directory.Exists(newsDir))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(newsDir, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.'))
            {
                continue;
            }

            var info = new FileInfo(path);
            target.Add(new PatchFileDto
            {
                Category = "news",
                Name = Path.GetRelativePath(newsDir, path).Replace('\\', '/'),
                Size = info.Length,
            });
        }
    }

    private static void AddLuaCategoryFiles(List<PatchFileDto> target, string luaDir)
    {
        foreach (var path in EnumerateLuaPatchFiles(luaDir).OrderBy(p => p, StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            target.Add(new PatchFileDto
            {
                Category = "lua",
                Name = Path.GetRelativePath(luaDir, path).Replace('\\', '/'),
                Size = info.Length,
            });
        }
    }

    internal static IEnumerable<string> EnumerateLuaPatchFiles(string luaDir)
    {
        if (!Directory.Exists(luaDir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(luaDir, "*", SearchOption.AllDirectories))
        {
            if (CategoryAccepts("lua", Path.GetFileName(file)))
            {
                yield return file;
            }
        }
    }

    private static void AddCategoryFiles(
        List<PatchFileDto> target,
        string dir,
        string category,
        string? stackRoot = null,
        string? patchKey = null)
    {
        var isMpq = string.Equals(category, "mpq", StringComparison.OrdinalIgnoreCase);
        foreach (var path in EnumerateCategoryFiles(dir, category).OrderBy(p => p, StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            target.Add(new PatchFileDto
            {
                Category = category,
                // Relative to the category dir so container sub-folders are preserved (e.g. "gems/Item.csv").
                Name = Path.GetRelativePath(dir, path).Replace('\\', '/'),
                Size = info.Length,
                Description = isMpq && stackRoot is not null && patchKey is not null
                    ? ResolveMpqFileDescription(stackRoot, patchKey, path)
                    : null
            });
        }
    }

    private static bool IsBaselineInitialized(string stackRoot)
    {
        var dir = MigrationLayout.ServerDbcDir(stackRoot);
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.dbc").Any();
    }

    // ===== Helpers: validation =====

    private static string ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name is required.");
        }

        // Reject any path separators or traversal; only a bare file name is allowed.
        var name = fileName.Trim();
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")
            || Path.IsPathRooted(name) || name != Path.GetFileName(name))
        {
            throw new ArgumentException($"Invalid file name: {fileName}");
        }

        return name;
    }

    /// <summary>
    /// Parses a category-relative path into an optional single container sub-folder and file name,
    /// enforcing "at most one folder deep" (for container categories) and extension safety.
    /// </summary>
    private static (string? Subfolder, string FileName) ParseCategoryPath(string normalizedCategory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("File name is required.");
        }

        var normalized = relativePath.Replace('\\', '/').Trim().Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            throw new ArgumentException("File name is required.");
        }

        string? subfolder = null;
        if (segments.Length == 2)
        {
            if (!AllowsContainers(normalizedCategory))
            {
                throw new ArgumentException($"Sub-folders are not allowed for '{normalizedCategory}'.");
            }

            subfolder = ValidateFileName(segments[0]);
        }
        else if (segments.Length > 2)
        {
            throw new ArgumentException(
                AllowsContainers(normalizedCategory)
                    ? $"Files may be nested at most one folder deep (got '{relativePath}')."
                    : $"Sub-folders are not allowed for '{normalizedCategory}'.");
        }

        var fileName = ValidateFileName(segments[^1]);
        if (!CategoryAccepts(normalizedCategory, fileName))
        {
            throw new ArgumentException($"File '{fileName}' is not allowed in category '{normalizedCategory}'.");
        }

        return (subfolder, fileName);
    }

    /// <summary>Resolves the on-disk path for a category-relative file, plus display metadata.</summary>
    private static (string Path, string NormalizedCategory, string DisplayName) ResolveCategoryFile(
        string stackRoot, string patchKey, string category, string relativePath)
    {
        var normalizedCategory = NormalizeCategory(category);
        var baseDir = ResolveCategoryDir(stackRoot, patchKey, category);
        var (subfolder, fileName) = ParseCategoryPath(normalizedCategory, relativePath);
        var dir = subfolder is null ? baseDir : System.IO.Path.Combine(baseDir, subfolder);
        var displayName = subfolder is null ? fileName : $"{subfolder}/{fileName}";
        return (System.IO.Path.Combine(dir, fileName), normalizedCategory, displayName);
    }

    private static string NormalizeCategory(string category)
    {
        var normalized = category.Replace('\\', '/').Trim().ToLowerInvariant();
        return normalized;
    }

    private static string ResolveCategoryDir(string stackRoot, string patchKey, string category)
    {
        var normalized = NormalizeCategory(category);
        return normalized switch
        {
            "dbc" => MigrationLayout.DbcDir(stackRoot, patchKey),
            "map" => MigrationLayout.MapDir(stackRoot, patchKey),
            "mpq" => MigrationLayout.MpqDir(stackRoot, patchKey),
            "config" => MigrationLayout.ConfigDir(stackRoot, patchKey),
            "lua" => MigrationLayout.PatchLuaDir(stackRoot, patchKey),
            "sql/world" => MigrationLayout.SqlDatabaseDir(stackRoot, patchKey, "world"),
            "sql/auth" => MigrationLayout.SqlDatabaseDir(stackRoot, patchKey, "auth"),
            "sql/characters" => MigrationLayout.SqlDatabaseDir(stackRoot, patchKey, "characters"),
            _ => throw new ArgumentException($"Unknown category: {category}")
        };
    }

    private async Task<ManagedStackEntity> GetStackAsync(string stackId, CancellationToken cancellationToken)
    {
        var stack = await _dbContext.ManagedStacks.SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        return stack ?? throw new KeyNotFoundException($"Stack not found: {stackId}");
    }

    private static Dictionary<string, DateTime> ParseAppliedPatches(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var records = JsonSerializer.Deserialize<List<AppliedPatchRecord>>(json, JsonOptions)
                ?? new List<AppliedPatchRecord>();
            return records
                .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().AppliedAt, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class AppliedPatchRecord
    {
        public string Key { get; set; } = string.Empty;
        public int Level { get; set; }
        public DateTime AppliedAt { get; set; }
    }
}

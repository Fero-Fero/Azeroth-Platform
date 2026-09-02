using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Patches;
using AzerothPlatform.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

public sealed class ServerWideProgressionService : IServerWideProgressionService
{
    private const string SettingsFileName = "individual_progression_settings.json";
    private const string ProgressionMetadataFileName = "progression.json";
    private const string SyncLogFileName = "progression_sync_log.json";
    private const string ReferenceManifestFileName = "progression_reference_manifest.json";
    private const string ProgressionRepoUrl = "https://github.com/Fero-Fero/Azeroth-Platform-Progression";
    private const string ProgressionRepoDefaultBranch = "master";
    private const string ProgressionRepoExpressBranch = "express-server";
    private const string IndividualProgressionRepoUrl = "https://github.com/Grimfeather/mod-individual-progression";
    private const string IndividualProgressionBranch = "master";
    private const string MappingFileName = "mapping.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly AzerothCoreDbContext _dbContext;
    private readonly IServerConfigService _serverConfig;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DockerOptions _dockerOptions;
    private readonly MigrationOptions _migrationOptions;
    private readonly ILogger<ServerWideProgressionService> _logger;

    public ServerWideProgressionService(
        AzerothCoreDbContext dbContext,
        IServerConfigService serverConfig,
        IHttpClientFactory httpClientFactory,
        IOptions<DockerOptions> dockerOptions,
        IOptions<MigrationOptions> migrationOptions,
        ILogger<ServerWideProgressionService> logger)
    {
        _dbContext = dbContext;
        _serverConfig = serverConfig;
        _httpClientFactory = httpClientFactory;
        _dockerOptions = dockerOptions.Value;
        _migrationOptions = migrationOptions.Value;
        _logger = logger;
    }

    public bool StackHasModule(IReadOnlyList<string> moduleIds) =>
        moduleIds.Contains(IServerWideProgressionService.ModuleId, StringComparer.OrdinalIgnoreCase);

    public async Task<ServerWideProgressionSettingsDto> GetSettingsAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        await EnsureModuleInstalledAsync(stackId, cancellationToken);
        return await LoadSettingsAsync(GetStackRoot(stackId), cancellationToken);
    }

    public async Task<ServerWideProgressionSettingsDto> DiscoverAndMergeSettingsAsync(
        string stackId,
        ServerWideProgressionSettingsDto? existing = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureModuleInstalledAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var settings = existing ?? await LoadSettingsAsync(stackRoot, cancellationToken);
        await DiscoverKeysAsync(stackId, settings, cancellationToken);
        await RefreshValuesAsync(stackId, settings, cancellationToken);
        await PersistSettingsAsync(stackRoot, settings, cancellationToken);
        return settings;
    }

    public async Task<ServerWideProgressionBootstrapResultDto> BootstrapAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        var stack = await EnsureModuleInstalledAsync(stackId, cancellationToken);
        if (stack.AppliedPatchLevel > 0)
        {
            throw new InvalidOperationException("Server-wide progression can only be prepared before any patch is applied.");
        }

        var stackRoot = GetStackRoot(stackId);
        var settings = await DiscoverAndMergeSettingsAsync(stackId, null, cancellationToken);
        settings.Bootstrapped = true;
        settings.ValidationBuildFingerprint = null;
        settings.ValidationPassedAt = null;
        settings.Values["Expansion"] = "0";
        settings.Values[settings.Keys.StartingProgression] = "1";
        settings.Values[settings.Keys.ProgressionLimit] = "1";
        settings.Values[settings.Keys.TbcRacesUnlockProgression] = "8";
        settings.Values[settings.Keys.TbcRacesStartingProgression] = "8";

        await WriteConfigFromSettingsAsync(stackId, settings, cancellationToken);
        await PersistSettingsAsync(stackRoot, settings, cancellationToken);

        _logger.LogInformation(
            "Bootstrapped Individual Progression for stack {StackId}. Run progression sync to create patch folders from Azeroth-Platform-Progression.",
            stackId);

        return new ServerWideProgressionBootstrapResultDto
        {
            TemplatesCreated = 0,
            ConfigUpdated = true,
            Expansion = 0,
            KeysDiscovered = true,
            Settings = settings,
        };
    }

    public async Task<ServerWideProgressionRecreatePatchesResultDto> RecreateMissingPatchesAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        var stack = await EnsureModuleInstalledAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var settings = await LoadSettingsAsync(stackRoot, cancellationToken);
        if (!settings.Bootstrapped)
        {
            throw new InvalidOperationException("Prepare server-wide progression before recreating patch templates.");
        }

        var repoPath = ResolveProgressionRepoDirectory(stackRoot);
        if (!Directory.Exists(repoPath))
        {
            throw new InvalidOperationException(
                $"Azeroth-Platform-Progression is not on the stack yet. Run progression sync first (expected at {repoPath}).");
        }

        var missingBefore = ProgressionRepoAlignment.CountMissingPatches(repoPath, stackRoot);
        RemovePlaceholderPatches(stackRoot);
        ProgressionRepoAlignment.RemoveOrphanedManagedPatches(repoPath, stackRoot);

        var createdPatchKeys = new List<string>();
        var templatesCreated = ProgressionRepoPatchSeeder.Seed(
            repoPath,
            stackRoot,
            onlyMissing: true,
            createdPatchKeys);

        if (createdPatchKeys.Count > 0)
        {
            await CopyRepoPatchesAsync(
                repoPath,
                stackRoot,
                new ProgressionSyncResultDto(),
                initialSync: true,
                new ProgressionSyncProgressStore(stackRoot),
                cancellationToken,
                limitToPatchKeys: createdPatchKeys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        settings.ValidationBuildFingerprint = null;
        settings.ValidationPassedAt = null;
        await PersistSettingsAsync(stackRoot, settings, cancellationToken);

        _logger.LogInformation(
            "Recreated {Count} missing Individual Progression patch templates for stack {StackId} ({MissingBefore} were missing).",
            templatesCreated, stackId, missingBefore);

        return new ServerWideProgressionRecreatePatchesResultDto
        {
            TemplatesCreated = templatesCreated,
            MissingBefore = missingBefore,
        };
    }

    public async Task OnPatchAppliedAsync(
        string stackId,
        string patchKey,
        PatchProgressionMetadataDto metadata,
        IList<string> applyLog,
        CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack not found: {stackId}");

        var moduleIds = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson, JsonOptions) ?? [];
        if (!StackHasModule(moduleIds))
        {
            return;
        }

        var settings = await LoadSettingsAsync(GetStackRoot(stackId), cancellationToken);
        if (!settings.Bootstrapped)
        {
            applyLog.Add("Individual Progression: settings not bootstrapped - skipping config sync.");
            return;
        }

        var keyNames = IndividualProgressionKeyNames.FromDto(settings.Keys);

        if (metadata.IncrementsProgression)
        {
            var starting = IncrementConfigValue(settings, keyNames.StartingProgression);
            var limit = IncrementConfigValue(settings, keyNames.ProgressionLimit);
            await SetConfigValueAsync(stackId, settings.ModuleConfPath, keyNames.StartingProgression, starting, cancellationToken);
            await SetConfigValueAsync(stackId, settings.ModuleConfPath, keyNames.ProgressionLimit, limit, cancellationToken);
            settings.Values[keyNames.StartingProgression] = starting;
            settings.Values[keyNames.ProgressionLimit] = limit;
            applyLog.Add($"Individual Progression: {keyNames.StartingProgression}={starting}, {keyNames.ProgressionLimit}={limit}");
        }
        else
        {
            applyLog.Add("Individual Progression: START patch applied - progression counters unchanged.");
        }

        var expansion = ResolveExpansionForPatch(metadata, patchKey);
        if (expansion is not null)
        {
            await SetConfigValueAsync(stackId, settings.WorldserverConfPath, settings.ExpansionKey, expansion, cancellationToken);
            settings.Values["Expansion"] = expansion;
            applyLog.Add($"Individual Progression: {settings.ExpansionKey}={expansion}");
        }

        await PersistSettingsAsync(GetStackRoot(stackId), settings, cancellationToken);
    }

    public Task<PatchProgressionMetadataDto?> ReadPatchMetadataAsync(string stackRoot, string patchKey)
    {
        var path = Path.Combine(MigrationLayout.PatchDir(stackRoot, patchKey), ProgressionMetadataFileName);
        if (!File.Exists(path))
        {
            return Task.FromResult<PatchProgressionMetadataDto?>(null);
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<PatchProgressionMetadataDto>(File.ReadAllText(path), JsonOptions);
            return Task.FromResult(metadata);
        }
        catch
        {
            return Task.FromResult<PatchProgressionMetadataDto?>(null);
        }
    }

    public int CountProgressionPatches(string stackRoot)
    {
        var expectedPatchKeys = ResolveExpectedPatchKeys(stackRoot);
        return expectedPatchKeys.Count > 0
            ? ProgressionRepoAlignment.CountAlignedPatches(expectedPatchKeys, stackRoot)
            : ProgressionRepoStructureValidator.CountManagedProgressionPatches(stackRoot);
    }

    public int GetExpectedProgressionPatchCount(string stackId)
    {
        var stackRoot = GetStackRoot(stackId);
        return ResolveExpectedPatchKeys(stackRoot).Count;
    }

    private static IReadOnlyList<string> ResolveExpectedPatchKeys(string stackRoot)
    {
        var manifest = LoadReferenceManifest(stackRoot);
        if (manifest?.ExpectedPatchKeys.Count > 0)
        {
            return manifest.ExpectedPatchKeys;
        }

        var repoPath = ResolveProgressionRepoDirectory(stackRoot);
        if (Directory.Exists(repoPath))
        {
            return ProgressionRepoAlignment.EnumerateExpectedPatchKeys(repoPath).ToList();
        }

        var syncLog = LoadSyncLog(stackRoot);
        return syncLog.LastKnownPatchKeys;
    }

    public async Task<ServerWideProgressionValidationResultDto> ValidatePatchesAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack not found: {stackId}");

        var stackRoot = GetStackRoot(stackId);
        var moduleIds = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson, JsonOptions) ?? [];
        var hasMip = StackHasModule(moduleIds);
        ServerWideProgressionSettingsDto? settings = null;
        if (hasMip)
        {
            settings = await LoadSettingsAsync(stackRoot, cancellationToken);
        }

        var syncLog = await LoadSyncLogAsync(stackRoot, cancellationToken);
        var hasCompletedSync = syncLog.LastSyncAt != default;
        var repoPath = ResolveProgressionRepoDirectory(stackRoot);
        var repoExists = Directory.Exists(repoPath);
        var validateRepoStructure = hasMip && hasCompletedSync;
        var mode = validateRepoStructure ? PatchValidationMode.Full : PatchValidationMode.ConfigOnly;
        var fingerprintMode = hasMip && settings is { Bootstrapped: true } && hasCompletedSync;

        var expectedPatchKeys = ResolveExpectedPatchKeys(stackRoot);

        var errors = new List<string>();
        var keyChecks = new List<ServerWideProgressionKeyCheckDto>();
        var patchCount = validateRepoStructure
            ? ProgressionRepoAlignment.CountAlignedPatches(expectedPatchKeys, stackRoot)
            : 0;
        var expectedPatchCount = validateRepoStructure
            ? expectedPatchKeys.Count
            : 0;

        if (hasMip && !hasCompletedSync)
        {
            errors.Add(
                "Progression sync has not completed yet. Run Sync with mod-individual-progression before validating patch structure.");
        }

        if (validateRepoStructure && expectedPatchKeys.Count == 0)
        {
            errors.Add(
                "No expected progression patch folders captured from Azeroth-Platform-Progression. Run Update & re-sync.");
        }

        if (validateRepoStructure)
        {
            var missingPatchCount = ProgressionRepoAlignment.CountMissingPatches(expectedPatchKeys, stackRoot);
            if (missingPatchCount > 0)
            {
                errors.Add(
                    $"Missing {missingPatchCount} progression patch folder(s) from Azeroth-Platform-Progression ({patchCount} of {expectedPatchCount} present on the stack). Run Update & re-sync.");
            }

            ProgressionRepoAlignment.ValidatePatchFolderAlignment(expectedPatchKeys, stackRoot, errors);

            if (repoExists)
            {
                ProgressionRepoStructureValidator.Validate(stackRoot, repoPath, errors);
            }
            else
            {
                var manifest = LoadReferenceManifest(stackRoot);
                if (manifest is not null)
                {
                    ProgressionRepoStructureValidator.ValidateAgainstManifest(stackRoot, manifest, errors);
                }
                else
                {
                    errors.Add(
                        "Progression reference manifest not found. Run Update & re-sync to capture the expected patch layout.");
                }
            }
        }

        await PatchConfigValidator.ValidateAsync(
            stackId,
            stackRoot,
            _serverConfig,
            errors,
            keyChecks,
            cancellationToken);

        var passed = errors.Count == 0
            && (keyChecks.Count == 0 || keyChecks.All(check => check.Exists && check.CanRead));

        if (validateRepoStructure
            && expectedPatchCount > 0
            && patchCount != expectedPatchCount
            && passed)
        {
            passed = false;
            errors.Add(
                $"Patch folder alignment mismatch: {patchCount} of {expectedPatchCount} expected progression patches are present with the correct Azeroth-Platform-Progression names.");
        }

        string? buildFingerprint = null;
        if (fingerprintMode)
        {
            buildFingerprint = ServerWideProgressionBuildFingerprint.Compute(stack);
            if (passed && buildFingerprint is null)
            {
                passed = false;
                errors.Add("Server build fingerprint is unavailable. Rebuild the server, then run validation again.");
            }
        }

        if (fingerprintMode && settings is not null)
        {
            if (passed)
            {
                settings.ValidationBuildFingerprint = buildFingerprint;
                settings.ValidationPassedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                settings.ValidationBuildFingerprint = null;
                settings.ValidationPassedAt = null;
            }

            await PersistSettingsAsync(stackRoot, settings, cancellationToken);
        }

        return new ServerWideProgressionValidationResultDto
        {
            Passed = passed,
            IsCurrent = fingerprintMode
                && passed
                && settings is not null
                && ServerWideProgressionBuildFingerprint.IsCurrent(settings, stack),
            Mode = mode,
            ValidatedAt = fingerprintMode ? settings?.ValidationPassedAt : DateTimeOffset.UtcNow,
            BuildFingerprint = fingerprintMode ? settings?.ValidationBuildFingerprint : null,
            PatchCount = patchCount,
            ExpectedPatchCount = expectedPatchCount,
            Errors = errors,
            KeyChecks = keyChecks,
        };
    }

    public async Task<(bool Allowed, string? Error)> CheckPatchApplyAllowedAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        var stack = await _dbContext.ManagedStacks.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken);
        if (stack is null)
        {
            return (false, $"Stack not found: {stackId}");
        }

        var moduleIds = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson, JsonOptions) ?? [];
        if (!StackHasModule(moduleIds))
        {
            return (true, null);
        }

        var settings = await LoadSettingsAsync(GetStackRoot(stackId), cancellationToken);
        if (!settings.Bootstrapped)
        {
            return (true, null);
        }

        if (stack.ServerType == ServerType.Express || ServerWideProgressionBuildFingerprint.IsCurrent(settings, stack))
        {
            return (true, null);
        }

        return (false,
            "Individual Progression patch validation is required. Run progression sync, rebuild the server if needed, then click Validate patches.");
    }

    /// <summary>
    /// 1.9 / 2.9 pre-patches raise worldserver Expansion so TBC/WotLK races and content can
    /// unlock before the 2.0 / 3.0 raid openers. Those content patches must not change it again.
    /// Express has no pre-patches, so catalog 2.0 PRE_TBC / 3.0 WOTLK_TIER_1 still raise it.
    /// </summary>
    internal static string? ResolveExpansionForPatch(
        PatchProgressionMetadataDto metadata,
        string? patchKey = null)
    {
        if (PatchFolderNames.TryParse(patchKey, out var index, out var label))
        {
            if (IsPrePatch(index, label))
            {
                return index.ExpansionRoot switch
                {
                    1 => "1",
                    2 => "2",
                    _ => null,
                };
            }

            if (index.IsExpansionBaseline && index.ExpansionRoot is 2 or 3 && !IsExpressCatalogOpener(label))
            {
                return null;
            }
        }

        return metadata.Expansion switch
        {
            "classic" when metadata.State == 0 => "0",
            "tbc" when metadata.State == 8 => "1",
            "wotlk" when metadata.State == 14 => "2",
            _ => null,
        };
    }

    private static bool IsPrePatch(PatchIndex index, string? label)
    {
        if (index.Sub1 == 9 && index.Sub2 == 0 && index.ComponentCount == 2)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        return label.Replace('-', ' ').Contains("pre patch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpressCatalogOpener(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return true;
        }

        var normalized = label.Trim().Replace(' ', '_').Replace('-', '_');
        return normalized.Equals("PRE_TBC", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("WOTLK_TIER_1", StringComparison.OrdinalIgnoreCase);
    }

    private static string IncrementConfigValue(ServerWideProgressionSettingsDto settings, string key)
    {
        var current = settings.Values.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed) ? parsed : 0;
        return (current + 1).ToString();
    }

    private static void RemovePlaceholderPatches(string stackRoot)
    {
        foreach (var placeholder in MigrationLayout.AllPlaceholderPatches)
        {
            var dir = MigrationLayout.PatchDir(stackRoot, placeholder);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to remove placeholder patch '{placeholder}': {ex.Message}", ex);
            }
        }
    }

    private async Task DiscoverKeysAsync(
        string stackId,
        ServerWideProgressionSettingsDto settings,
        CancellationToken cancellationToken)
    {
        settings.ModuleConfPath = await ResolveModuleConfPathAsync(stackId, settings.ModuleConfPath, cancellationToken);
        var content = await ReadConfigContentAsync(stackId, settings.ModuleConfPath, cancellationToken);
        var discovered = ServerConfigValueEditor.GrepIndividualProgressionKeys(content);
        var keyNames = IndividualProgressionKeyNames.FromDto(settings.Keys);
        ServerConfigValueEditor.ApplyKeyMapping(keyNames, discovered);
        settings.Keys = keyNames.ToDto();
    }

    private async Task RefreshValuesAsync(
        string stackId,
        ServerWideProgressionSettingsDto settings,
        CancellationToken cancellationToken)
    {
        var moduleContent = await ReadConfigContentAsync(stackId, settings.ModuleConfPath, cancellationToken);
        foreach (var key in new[]
                 {
                     settings.Keys.StartingProgression,
                     settings.Keys.ProgressionLimit,
                     settings.Keys.TbcRacesUnlockProgression,
                     settings.Keys.TbcRacesStartingProgression,
                 })
        {
            if (ServerConfigValueEditor.TryGetValue(moduleContent, key, out var value))
            {
                settings.Values[key] = value;
            }
        }

        var worldContent = await ReadConfigContentAsync(stackId, settings.WorldserverConfPath, cancellationToken);
        if (ServerConfigValueEditor.TryGetValue(worldContent, settings.ExpansionKey, out var expansion))
        {
            settings.Values["Expansion"] = expansion;
        }
    }

    private async Task WriteConfigFromSettingsAsync(
        string stackId,
        ServerWideProgressionSettingsDto settings,
        CancellationToken cancellationToken)
    {
        if (settings.Values.TryGetValue("Expansion", out var expansion))
        {
            await SetConfigValueAsync(stackId, settings.WorldserverConfPath, settings.ExpansionKey, expansion, cancellationToken);
        }

        foreach (var (key, value) in settings.Values)
        {
            if (string.Equals(key, "Expansion", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await SetConfigValueAsync(stackId, settings.ModuleConfPath, key, value, cancellationToken);
        }
    }

    private async Task SetConfigValueAsync(
        string stackId,
        string relativePath,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        var current = await _serverConfig.ReadAsync(stackId, relativePath, cancellationToken);
        var updated = ServerConfigValueEditor.SetValue(current.Content, key, value);
        await _serverConfig.SaveAsync(stackId, relativePath, updated, cancellationToken);
    }

    private async Task<string> ReadConfigContentAsync(string stackId, string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            return (await _serverConfig.ReadAsync(stackId, relativePath, cancellationToken)).Content;
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
    }

    private async Task<string> ResolveModuleConfPathAsync(
        string stackId,
        string current,
        CancellationToken cancellationToken)
    {
        try
        {
            var files = await _serverConfig.ListAsync(stackId, cancellationToken);
            var match = files.Files.FirstOrDefault(file =>
                file.Path.Contains("individual", StringComparison.OrdinalIgnoreCase)
                && file.Path.EndsWith(".conf", StringComparison.OrdinalIgnoreCase));
            return match?.Path ?? current;
        }
        catch
        {
            return current;
        }
    }

    private async Task<ServerWideProgressionSettingsDto> LoadSettingsAsync(
        string stackRoot,
        CancellationToken cancellationToken)
    {
        var path = SettingsFilePath(stackRoot);
        if (!File.Exists(path))
        {
            return new ServerWideProgressionSettingsDto();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ServerWideProgressionSettingsDto>(stream, JsonOptions, cancellationToken)
            ?? new ServerWideProgressionSettingsDto();
    }

    private static Task PersistSettingsAsync(
        string stackRoot,
        ServerWideProgressionSettingsDto settings,
        CancellationToken cancellationToken)
    {
        var path = SettingsFilePath(stackRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings, JsonOptions), cancellationToken);
    }

    private static string SettingsFilePath(string stackRoot) =>
        Path.Combine(stackRoot, SettingsFileName);

    private async Task<ManagedStackEntity> EnsureModuleInstalledAsync(string stackId, CancellationToken cancellationToken)
    {
        var stack = await _dbContext.ManagedStacks.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == stackId, cancellationToken)
            ?? throw new KeyNotFoundException($"Stack not found: {stackId}");

        var moduleIds = JsonSerializer.Deserialize<List<string>>(stack.ModuleIdsJson, JsonOptions) ?? [];
        if (!StackHasModule(moduleIds))
        {
            throw new InvalidOperationException(
                $"Stack must have {IServerWideProgressionService.ModuleId} installed to use Server Wide Progression features.");
        }

        return stack;
    }

    private string GetStackRoot(string stackId)
    {
        var baseDir = Path.IsPathRooted(_dockerOptions.BuildsPath)
            ? _dockerOptions.BuildsPath
            : Path.GetFullPath(_dockerOptions.BuildsPath);
        return Path.Combine(baseDir, stackId);
    }

    private async Task<bool> IsExpressStackRootAsync(string stackRoot, CancellationToken cancellationToken)
    {
        var stackId = Path.GetFileName(stackRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(stackId))
        {
            return false;
        }

        var stack = await _dbContext.ManagedStacks.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == stackId, cancellationToken);
        return stack?.ServerType == ServerType.Express;
    }

    // ===== Progression Sync =====

    public async Task<ProgressionSyncStatusDto> GetSyncStatusAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        var stackRoot = GetStackRoot(stackId);
        var logPath = SyncLogPath(stackRoot);
        var status = new ProgressionSyncStatusDto();

        if (File.Exists(logPath))
        {
            status.HasOptionalFilesLog = true;
            var log = await LoadSyncLogAsync(stackRoot, cancellationToken);
            status.IgnoredFilesCount = log.Entries.Count(e => !e.Accepted);
            status.LastSyncAt = log.LastSyncAt == default ? null : log.LastSyncAt;
            status.HasCompletedInitialSync = log.LastSyncAt != default;
        }

        var progress = await ProgressionSyncProgressStore.TryLoadAsync(stackRoot, cancellationToken);
        if (progress is not null)
        {
            if (ProgressionSyncProgressStore.IsStale(progress, stackRoot))
            {
                var progressStore = new ProgressionSyncProgressStore(stackRoot);
                await progressStore.CompleteAsync(
                    false,
                    "Progression sync timed out or was interrupted.",
                    "Progression sync timed out or was interrupted.",
                    cancellationToken);
                progress = await ProgressionSyncProgressStore.TryLoadAsync(stackRoot, cancellationToken);
            }

            if (progress is not null)
            {
                ApplyProgressToStatus(status, progress, stackRoot);
            }
        }

        return status;
    }

    private static void ApplyProgressToStatus(
        ProgressionSyncStatusDto status,
        ProgressionSyncProgressState progress,
        string stackRoot)
    {
        if (ProgressionSyncProgressStore.IsStale(progress, stackRoot))
        {
            status.IsRunning = false;
            status.Phase = "Failed";
            status.ProgressPercent = 0;
            status.Message = "Progression sync timed out or was interrupted.";
            status.Error = status.Message;
            status.Log = progress.Log;
            return;
        }

        status.IsRunning = progress.IsRunning;
        status.Phase = progress.Phase;
        status.ProgressPercent = progress.ProgressPercent;
        status.Message = progress.Message;
        status.StartedAt = progress.StartedAt;
        status.CompletedAt = progress.CompletedAt;
        status.Error = progress.Error;
        if (progress.Log.Count > 0)
        {
            status.Log = progress.Log;
        }
    }

    public async Task<ProgressionSyncResultDto> RunSyncAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        var stack = await EnsureModuleInstalledAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var result = new ProgressionSyncResultDto();
        var progressStore = new ProgressionSyncProgressStore(stackRoot);

        var existingProgress = await ProgressionSyncProgressStore.TryLoadAsync(stackRoot, cancellationToken);
        if (ProgressionSyncProgressStore.IsActivelyRunning(existingProgress, stackRoot))
        {
            result.Error = "A progression sync is already running for this stack.";
            return result;
        }

        await progressStore.StartAsync(cancellationToken);

        try
        {
            var moduleRoot = Path.Combine(stackRoot, "azerothcore-wotlk", "modules", IServerWideProgressionService.ModuleId);
            var moduleError = await EnsureIndividualProgressionModuleAsync(
                moduleRoot,
                progressStore,
                result,
                cancellationToken);
            if (moduleError is not null)
            {
                result.Error = moduleError;
                await progressStore.CompleteAsync(false, result.Error, result.Error, cancellationToken);
                return result;
            }

            var (repoDir, repoError) = await EnsureProgressionRepoAsync(
                stackRoot,
                progressStore,
                result,
                cancellationToken);
            if (repoError is not null)
            {
                result.Error = repoError;
                await progressStore.CompleteAsync(false, result.Error, result.Error, cancellationToken);
                return result;
            }

            var mappingPath = Path.Combine(repoDir, MappingFileName);
            if (!File.Exists(mappingPath))
            {
                result.Error = "mapping.json not found in Azeroth-Platform-Progression repository.";
                await progressStore.CompleteAsync(false, result.Error, result.Error, cancellationToken);
                return result;
            }

            var mappingJson = await File.ReadAllTextAsync(mappingPath, cancellationToken);
            var mapping = JsonSerializer.Deserialize<ProgressionSyncMappingDto>(mappingJson, JsonOptions)
                ?? new ProgressionSyncMappingDto();

            var log = await LoadSyncLogAsync(stackRoot, cancellationToken);
            var initialSync = ProgressionSyncTargetPolicy.IsInitialSync(log);

            await progressStore.ReportAsync(
                "Preparing patches",
                40,
                initialSync
                    ? "Creating progression patch folders from Azeroth-Platform-Progression…"
                    : "Ensuring progression patch folders exist from Azeroth-Platform-Progression…",
                cancellationToken);

            if (initialSync)
            {
                RemovePlaceholderPatches(stackRoot);
            }

            var removedOrphans = ProgressionRepoAlignment.RemoveOrphanedManagedPatches(repoDir, stackRoot, result.Log);
            if (removedOrphans > 0)
            {
                var orphanMessage =
                    $"Removed {removedOrphans} orphaned progression patch folder(s) that are not in Azeroth-Platform-Progression.";
                result.Log.Add(orphanMessage);
                await progressStore.ReportAsync("Preparing patches", 42, orphanMessage, cancellationToken);
            }

            var createdPatchKeys = new List<string>();
            var templatesPrepared = ProgressionRepoPatchSeeder.Seed(
                repoDir,
                stackRoot,
                onlyMissing: !initialSync,
                createdPatchKeys);

            result.NewlyCreatedPatchKeys.AddRange(createdPatchKeys);

            if (initialSync)
            {
                var message =
                    $"Initial sync: prepared {templatesPrepared} progression patch folder(s) from Azeroth-Platform-Progression; existing patch content in sync targets will be overwritten.";
                result.Log.Add(message);
                await progressStore.ReportAsync("Preparing patches", 45, message, cancellationToken);
            }
            else if (templatesPrepared > 0)
            {
                var message = $"Ensured {templatesPrepared} missing progression patch folder(s) from Azeroth-Platform-Progression.";
                result.Log.Add(message);
                await progressStore.ReportAsync("Preparing patches", 45, message, cancellationToken);
                result.Log.Add("Update sync: only managed progression patches are updated; custom patches are left unchanged.");
            }
            else
            {
                result.Log.Add("Update sync: only managed progression patches are updated; custom patches are left unchanged.");
            }

            if (!initialSync && createdPatchKeys.Count > 0)
            {
                await CopyRepoPatchesAsync(
                    repoDir,
                    stackRoot,
                    result,
                    initialSync: false,
                    progressStore,
                    cancellationToken,
                    limitToPatchKeys: createdPatchKeys.ToHashSet(StringComparer.OrdinalIgnoreCase));
            }

            await CopyRepoPatchesAsync(
                repoDir,
                stackRoot,
                result,
                initialSync,
                progressStore,
                cancellationToken);

            if (createdPatchKeys.Count > 0)
            {
                var settings = await LoadSettingsAsync(stackRoot, cancellationToken);
                settings.ValidationBuildFingerprint = null;
                settings.ValidationPassedAt = null;
                await PersistSettingsAsync(stackRoot, settings, cancellationToken);
            }

            if (createdPatchKeys.Count > 0 && stack.AppliedPatchLevel > 0)
            {
                result.ReapplyAllRecommended = true;
                result.ReapplyAllReason =
                    $"Progression sync added {createdPatchKeys.Count} new patch(es). Reapply all patches so SQL, DBC, config, and launcher changes from the new tiers take effect.";
                result.Log.Add(result.ReapplyAllReason);
            }

            var mappingCount = Math.Max(1, mapping.Mappings.Count);
            for (var i = 0; i < mapping.Mappings.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessMappingEntry(
                    mapping.Mappings[i],
                    moduleRoot,
                    stackRoot,
                    log,
                    result,
                    initialSync);
                var percent = 70 + (int)(20.0 * (i + 1) / mappingCount);
                await progressStore.ReportAsync(
                    "Applying module mappings",
                    percent,
                    $"Imported mod-individual-progression mapping {i + 1} of {mapping.Mappings.Count}.",
                    cancellationToken);
            }

            log.LastSyncAt = DateTimeOffset.UtcNow;
            log.LastKnownPatchKeys = ProgressionRepoAlignment.EnumerateExpectedPatchKeys(repoDir).ToList();
            var referenceManifest = ProgressionReferenceManifestBuilder.BuildFromRepo(repoDir);
            await PersistReferenceManifestAsync(stackRoot, referenceManifest, cancellationToken);
            await PersistSyncLogAsync(stackRoot, log, cancellationToken);

            await progressStore.ReportAsync(
                "Cleaning patch folders",
                96,
                "Extracting leftover archives and removing duplicate files from patch folders…",
                cancellationToken);
            var cleanup = ProgressionPatchPostSyncCleanup.Run(stackRoot, result.Log);

            TryPruneProgressionRepo(stackRoot, result.Log);
            result.Success = true;

            var cleanupSummary = cleanup.TotalRemoved > 0
                ? $" Extracted {cleanup.ArchivesExtracted} archive(s), removed {cleanup.DuplicateFilesRemoved} duplicate file(s)."
                : string.Empty;
            var successMessage =
                $"Sync complete: {result.CopiedFiles} file(s) copied, {result.PendingOptionalFiles.Count} optional file(s) pending.{cleanupSummary}";
            result.Log.Add(successMessage);
            await progressStore.CompleteAsync(true, successMessage, null, cancellationToken);

            _logger.LogInformation(
                "Progression sync completed for stack {StackId}: {Copied} files copied, {Pending} pending optional files.",
                stackId, result.CopiedFiles, result.PendingOptionalFiles.Count);
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            result.Log.Add(ex.Message);
            await progressStore.CompleteAsync(false, "Progression sync failed.", ex.Message, cancellationToken);
            _logger.LogError(ex, "Progression sync failed for stack {StackId}.", stackId);
        }

        return result;
    }

    public async Task<ProgressionSyncResultDto> ResolveOptionalFilesAsync(
        string stackId,
        ResolveOptionalFilesRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureModuleInstalledAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var log = await LoadSyncLogAsync(stackRoot, cancellationToken);
        var result = new ProgressionSyncResultDto();
        var moduleRoot = Path.Combine(stackRoot, "azerothcore-wotlk", "modules", "mod-individual-progression");

        foreach (var (source, accepted) in request.Decisions)
        {
            var entry = log.Entries.FirstOrDefault(e =>
                string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                result.Log.Add($"Unknown source in sync log: {source}");
                continue;
            }

            entry.Accepted = accepted;
            entry.DecidedAt = DateTimeOffset.UtcNow;

            if (accepted)
            {
                var sourceFile = Path.Combine(
                    moduleRoot,
                    ProgressionPatchFolderResolver.NormalizeModuleSourcePath(source));
                var resolvedDir = ProgressionPatchFolderResolver.Resolve(stackRoot, entry.Destination);

                if (resolvedDir is not null && File.Exists(sourceFile))
                {
                    Directory.CreateDirectory(resolvedDir);
                    File.Copy(sourceFile, Path.Combine(resolvedDir, entry.FileName), overwrite: true);
                    result.CopiedFiles++;
                    result.Log.Add($"Copied {entry.FileName} to {entry.Destination}.");
                }
                else
                {
                    result.Log.Add($"Source file not available: {source}");
                }
            }
            else
            {
                result.SkippedOptional++;
                result.Log.Add($"Ignored {entry.FileName}.");
            }
        }

        await PersistSyncLogAsync(stackRoot, log, cancellationToken);
        result.Success = true;
        return result;
    }

    public async Task<IReadOnlyList<ProgressionIgnoredFileDto>> GetIgnoredFilesAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        var stackRoot = GetStackRoot(stackId);
        var log = await LoadSyncLogAsync(stackRoot, cancellationToken);

        return log.Entries
            .Where(e => !e.Accepted)
            .Select(e => new ProgressionIgnoredFileDto
            {
                Source = e.Source,
                Destination = e.Destination,
                FileName = e.FileName,
                DecidedAt = e.DecidedAt,
            })
            .ToList();
    }

    public async Task<ProgressionSyncResultDto> RepromptIgnoredFileAsync(
        string stackId,
        string source,
        CancellationToken cancellationToken = default)
    {
        await EnsureModuleInstalledAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var log = await LoadSyncLogAsync(stackRoot, cancellationToken);
        var result = new ProgressionSyncResultDto();

        var entry = log.Entries.FirstOrDefault(e =>
            string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            result.Error = $"File not found in sync log: {source}";
            return result;
        }

        entry.Accepted = true;
        entry.DecidedAt = DateTimeOffset.UtcNow;

        var moduleRoot = Path.Combine(stackRoot, "azerothcore-wotlk", "modules", "mod-individual-progression");
        var sourceFile = Path.Combine(
            moduleRoot,
            ProgressionPatchFolderResolver.NormalizeModuleSourcePath(source));
        var resolvedDir = ProgressionPatchFolderResolver.Resolve(stackRoot, entry.Destination);

        if (resolvedDir is not null && File.Exists(sourceFile))
        {
            Directory.CreateDirectory(resolvedDir);
            File.Copy(sourceFile, Path.Combine(resolvedDir, entry.FileName), overwrite: true);
            result.CopiedFiles = 1;
            result.Log.Add($"Copied {entry.FileName} to {entry.Destination}.");
        }
        else
        {
            result.Log.Add("Source file not available on disk; marked as accepted for next sync.");
        }

        await PersistSyncLogAsync(stackRoot, log, cancellationToken);
        result.Success = true;
        return result;
    }

    // ===== Progression Sync Helpers =====

    private static string ResolveProgressionRepoDirectory(string stackRoot) =>
        ProgressionRepoPathResolver.Resolve(stackRoot);

    private async Task<string?> EnsureIndividualProgressionModuleAsync(
        string moduleRoot,
        ProgressionSyncProgressStore progressStore,
        ProgressionSyncResultDto result,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(moduleRoot))
        {
            return "mod-individual-progression is not present in the stack build. Rebuild the stack first.";
        }

        await progressStore.ReportAsync(
            "Updating module",
            5,
            "Pulling latest mod-individual-progression changes…",
            cancellationToken);

        var (changed, error) = await GitRepoSync.EnsureLatestAsync(
            moduleRoot,
            IndividualProgressionRepoUrl,
            IndividualProgressionBranch,
            cancellationToken);
        if (error is not null)
        {
            return $"Failed to update mod-individual-progression: {error}";
        }

        result.Log.Add(changed
            ? "Updated mod-individual-progression from GitHub."
            : "mod-individual-progression already up to date.");
        return null;
    }

    private async Task<(string RepoDir, string? Error)> EnsureProgressionRepoAsync(
        string stackRoot,
        ProgressionSyncProgressStore progressStore,
        ProgressionSyncResultDto result,
        CancellationToken cancellationToken)
    {
        var repoDir = ResolveProgressionRepoDirectory(stackRoot);
        var expressBranch = await IsExpressStackRootAsync(stackRoot, cancellationToken);
        var branch = expressBranch ? ProgressionRepoExpressBranch : ProgressionRepoDefaultBranch;

        await progressStore.ReportAsync(
            "Updating repository",
            15,
            "Pulling latest Azeroth-Platform-Progression changes…",
            cancellationToken);

        var (changed, error) = await GitRepoSync.EnsureLatestAsync(
            repoDir,
            ProgressionRepoUrl,
            branch,
            cancellationToken);
        if (error is not null)
        {
            return (repoDir, $"Failed to update progression repo: {error}");
        }

        result.Log.Add(changed
            ? "Updated Azeroth-Platform-Progression repository from GitHub."
            : "Azeroth-Platform-Progression already up to date.");
        await progressStore.ReportAsync(
            "Loading mapping",
            30,
            "Updated Azeroth-Platform-Progression repository.",
            cancellationToken);
        return (repoDir, null);
    }

    private static string SyncLogPath(string stackRoot) =>
        Path.Combine(stackRoot, SyncLogFileName);

    private static string ReferenceManifestPath(string stackRoot) =>
        Path.Combine(stackRoot, ReferenceManifestFileName);

    private static ProgressionReferenceManifestDto? LoadReferenceManifest(string stackRoot)
    {
        var path = ReferenceManifestPath(stackRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProgressionReferenceManifestDto>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task PersistReferenceManifestAsync(
        string stackRoot,
        ProgressionReferenceManifestDto manifest,
        CancellationToken cancellationToken)
    {
        var path = ReferenceManifestPath(stackRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
    }

    private static ProgressionOptionalFilesLogDto LoadSyncLog(string stackRoot)
    {
        var path = SyncLogPath(stackRoot);
        if (!File.Exists(path))
        {
            return new ProgressionOptionalFilesLogDto();
        }

        return JsonSerializer.Deserialize<ProgressionOptionalFilesLogDto>(File.ReadAllText(path), JsonOptions)
            ?? new ProgressionOptionalFilesLogDto();
    }

    private static void TryPruneProgressionRepo(string stackRoot, ICollection<string> log)
    {
        var repoDir = ResolveProgressionRepoDirectory(stackRoot);
        if (!Directory.Exists(repoDir))
        {
            return;
        }

        try
        {
            Directory.Delete(repoDir, recursive: true);
            log.Add(
                "Removed Azeroth-Platform-Progression checkout from the stack to save disk space. Patch validation uses the synced reference manifest.");
        }
        catch (Exception ex)
        {
            log.Add($"Failed to remove Azeroth-Platform-Progression checkout: {ex.Message}");
        }
    }

    private static async Task<ProgressionOptionalFilesLogDto> LoadSyncLogAsync(
        string stackRoot,
        CancellationToken cancellationToken)
    {
        var path = SyncLogPath(stackRoot);
        if (!File.Exists(path))
        {
            return new ProgressionOptionalFilesLogDto();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProgressionOptionalFilesLogDto>(stream, JsonOptions, cancellationToken)
            ?? new ProgressionOptionalFilesLogDto();
    }

    private static Task PersistSyncLogAsync(
        string stackRoot,
        ProgressionOptionalFilesLogDto log,
        CancellationToken cancellationToken)
    {
        var path = SyncLogPath(stackRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(log, JsonOptions), cancellationToken);
    }

    private static void ProcessMappingEntry(
        ProgressionSyncMappingEntryDto entry,
        string moduleRoot,
        string stackRoot,
        ProgressionOptionalFilesLogDto log,
        ProgressionSyncResultDto result,
        bool initialSync)
    {
        var resolvedDestDir = ProgressionPatchFolderResolver.Resolve(stackRoot, entry.Destination);
        if (resolvedDestDir is null)
        {
            return;
        }

        if (!ProgressionSyncTargetPolicy.ShouldApplySyncToPath(stackRoot, resolvedDestDir, initialSync, result.Log))
        {
            return;
        }

        Directory.CreateDirectory(resolvedDestDir);

        var sourcePath = ProgressionPatchFolderResolver.NormalizeModuleSourcePath(entry.Source);

        if (sourcePath.Contains('*'))
        {
            var sourceDir = Path.Combine(moduleRoot, Path.GetDirectoryName(sourcePath) ?? string.Empty);
            if (!Directory.Exists(sourceDir))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                var fileName = Path.GetFileName(file);
                var dirPart = Path.GetDirectoryName(sourcePath)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
                var fileSourcePath = string.IsNullOrEmpty(dirPart) ? fileName : $"{dirPart}/{fileName}";
                var destFile = Path.Combine(resolvedDestDir, fileName);
                CopySyncFile(file, destFile, fileSourcePath, entry.Destination, fileName, entry.Optional, log, result);
            }
        }
        else
        {
            var sourceFile = Path.Combine(moduleRoot, sourcePath);
            if (!File.Exists(sourceFile))
            {
                return;
            }

            var fileName = Path.GetFileName(sourceFile);
            var destFile = Path.Combine(resolvedDestDir, fileName);
            CopySyncFile(sourceFile, destFile, sourcePath, entry.Destination, fileName, entry.Optional, log, result);
        }
    }

    private static void CopySyncFile(
        string sourceFile,
        string destFile,
        string sourcePath,
        string destination,
        string fileName,
        bool optional,
        ProgressionOptionalFilesLogDto log,
        ProgressionSyncResultDto result)
    {
        if (!optional)
        {
            File.Copy(sourceFile, destFile, overwrite: true);
            result.CopiedFiles++;
            return;
        }

        if (File.Exists(destFile))
        {
            result.SkippedOptional++;
            return;
        }

        var existing = log.Entries.FirstOrDefault(e =>
            string.Equals(e.Source, sourcePath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (existing.Accepted)
            {
                File.Copy(sourceFile, destFile, overwrite: true);
                result.CopiedFiles++;
            }
            else
            {
                result.SkippedOptional++;
            }

            return;
        }

        log.Entries.Add(new ProgressionOptionalFileEntryDto
        {
            Source = sourcePath,
            Destination = destination,
            FileName = fileName,
            Accepted = false,
            DecidedAt = DateTimeOffset.UtcNow,
        });

        result.PendingOptionalFiles.Add(new ProgressionSyncPendingFileDto
        {
            Source = sourcePath,
            Destination = destination,
            FileName = fileName,
        });
        result.SkippedOptional++;
    }

    private static async Task CopyRepoPatchesAsync(
        string repoDir,
        string stackRoot,
        ProgressionSyncResultDto result,
        bool initialSync,
        ProgressionSyncProgressStore progressStore,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? limitToPatchKeys = null)
    {
        var filesToCopy = new List<(string SourcePath, string DestDir, string FileName, string RelativePath)>();

        foreach (var expansionName in MigrationLayout.ExpansionRoots.Keys)
        {
            var expansionDir = Directory.EnumerateDirectories(repoDir)
                .FirstOrDefault(d => string.Equals(
                    Path.GetFileName(d), expansionName, StringComparison.OrdinalIgnoreCase));

            if (expansionDir is null)
            {
                continue;
            }

            foreach (var patchDir in Directory.EnumerateDirectories(expansionDir))
            {
                var patchName = Path.GetFileName(patchDir);

                foreach (var file in Directory.EnumerateFiles(patchDir, "*", SearchOption.AllDirectories))
                {
                    var relativeToPatch = Path.GetRelativePath(patchDir, file);
                    var categoryPath = ProgressionRepoStructureValidator.NormalizeRepoCategoryPath(
                        Path.GetDirectoryName(relativeToPatch)?.Replace(Path.DirectorySeparatorChar, '/'));

                    var destination = string.IsNullOrEmpty(categoryPath)
                        ? $"{Path.GetFileName(expansionDir)}/{patchName}/"
                        : $"{Path.GetFileName(expansionDir)}/{patchName}/{categoryPath}/";

                    var resolvedDir = ProgressionPatchFolderResolver.Resolve(stackRoot, destination);
                    if (resolvedDir is null)
                    {
                        continue;
                    }

                    if (limitToPatchKeys is not null
                        && !PatchKeyMatchesLimit(stackRoot, resolvedDir, limitToPatchKeys))
                    {
                        continue;
                    }

                    if (!ProgressionSyncTargetPolicy.ShouldApplySyncToPath(stackRoot, resolvedDir, initialSync, result.Log))
                    {
                        continue;
                    }

                    filesToCopy.Add((file, resolvedDir, Path.GetFileName(file), relativeToPatch));
                }
            }
        }

        await progressStore.ReportAsync(
            "Copying progression repository",
            55,
            $"Copying {filesToCopy.Count} file(s) from Azeroth-Platform-Progression…",
            cancellationToken);

        var total = Math.Max(1, filesToCopy.Count);
        for (var i = 0; i < filesToCopy.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (sourcePath, destDir, fileName, _) = filesToCopy[i];
            Directory.CreateDirectory(destDir);
            File.Copy(sourcePath, Path.Combine(destDir, fileName), overwrite: true);
            result.CopiedFiles++;

            if (i == filesToCopy.Count - 1 || (i + 1) % 25 == 0)
            {
                var percent = 65 + (int)(30.0 * (i + 1) / total);
                await progressStore.ReportAsync(
                    "Copying progression repository",
                    percent,
                    $"Copied {i + 1} of {filesToCopy.Count} repository file(s).",
                    cancellationToken);
            }
        }

        await progressStore.ReportAsync(
            "Finalizing",
            95,
            "Finalizing progression sync…",
            cancellationToken);
    }

    private static bool PatchKeyMatchesLimit(
        string stackRoot,
        string resolvedPath,
        IReadOnlySet<string> limitToPatchKeys)
    {
        var migrationsRoot = MigrationLayout.MigrationsRoot(stackRoot);
        var relative = Path.GetRelativePath(migrationsRoot, resolvedPath);
        var patchKey = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return limitToPatchKeys.Contains(patchKey);
    }
}

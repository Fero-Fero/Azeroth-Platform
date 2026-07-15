using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzerothPlatform.Infrastructure.Services.IndividualProgression;

public sealed class IndividualProgressionSyncService : IIndividualProgressionSyncService
{
    private const string SettingsFileName = "individual_progression_settings.json";
    private const string ProgressionMetadataFileName = "progression.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly AzerothCoreDbContext _dbContext;
    private readonly IServerConfigService _serverConfig;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DockerOptions _dockerOptions;
    private readonly ILogger<IndividualProgressionSyncService> _logger;

    public IndividualProgressionSyncService(
        AzerothCoreDbContext dbContext,
        IServerConfigService serverConfig,
        IHttpClientFactory httpClientFactory,
        IOptions<DockerOptions> dockerOptions,
        ILogger<IndividualProgressionSyncService> logger)
    {
        _dbContext = dbContext;
        _serverConfig = serverConfig;
        _httpClientFactory = httpClientFactory;
        _dockerOptions = dockerOptions.Value;
        _logger = logger;
    }

    public bool StackHasModule(IReadOnlyList<string> moduleIds) =>
        moduleIds.Contains(IIndividualProgressionSyncService.ModuleId, StringComparer.OrdinalIgnoreCase);

    public async Task<IndividualProgressionSettingsDto> GetSettingsAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        await EnsureModuleInstalledAsync(stackId, cancellationToken);
        return await LoadSettingsAsync(GetStackRoot(stackId), cancellationToken);
    }

    public async Task<IndividualProgressionSettingsDto> SaveSettingsAsync(
        string stackId,
        IndividualProgressionSettingsDto settings,
        CancellationToken cancellationToken = default)
    {
        await EnsureModuleInstalledAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        await PersistSettingsAsync(stackRoot, settings, cancellationToken);
        await WriteConfigFromSettingsAsync(stackId, settings, cancellationToken);
        return settings;
    }

    public async Task<IndividualProgressionSettingsDto> DiscoverAndMergeSettingsAsync(
        string stackId,
        IndividualProgressionSettingsDto? existing = null,
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

    public async Task<IndividualProgressionBootstrapResultDto> BootstrapAsync(
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

        var templatesCreated = SeedProgressionPatches(stackRoot, onlyMissing: false);

        _logger.LogInformation(
            "Bootstrapped Individual Progression for stack {StackId}: {Count} patch templates",
            stackId, templatesCreated);

        return new IndividualProgressionBootstrapResultDto
        {
            TemplatesCreated = templatesCreated,
            ConfigUpdated = true,
            Expansion = 0,
            KeysDiscovered = true,
            Settings = settings,
        };
    }

    public async Task<IndividualProgressionRecreatePatchesResultDto> RecreateMissingPatchesAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        await EnsureModuleInstalledAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var settings = await LoadSettingsAsync(stackRoot, cancellationToken);
        if (!settings.Bootstrapped)
        {
            throw new InvalidOperationException("Prepare server-wide progression before recreating patch templates.");
        }

        var missingBefore = IndividualProgressionPatchCatalog.All.Count(definition =>
            !ProgressionPatchExists(stackRoot, definition));
        RemovePlaceholderPatches(stackRoot);
        var templatesCreated = SeedProgressionPatches(stackRoot, onlyMissing: true);

        settings.ValidationBuildFingerprint = null;
        settings.ValidationPassedAt = null;
        await PersistSettingsAsync(stackRoot, settings, cancellationToken);

        _logger.LogInformation(
            "Recreated {Count} missing Individual Progression patch templates for stack {StackId} ({MissingBefore} were missing).",
            templatesCreated, stackId, missingBefore);

        return new IndividualProgressionRecreatePatchesResultDto
        {
            TemplatesCreated = templatesCreated,
            MissingBefore = missingBefore,
        };
    }

    public async Task OnPatchAppliedAsync(
        string stackId,
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
            applyLog.Add("Individual Progression: settings not bootstrapped — skipping config sync.");
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
            applyLog.Add("Individual Progression: START patch applied — progression counters unchanged.");
        }

        var expansion = ResolveExpansionForPatch(metadata);
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

    public int CountProgressionPatches(string stackRoot) =>
        IndividualProgressionPatchCatalog.All.Count(definition => ProgressionPatchExists(stackRoot, definition));

    public async Task<IndividualProgressionValidationResultDto> ValidatePatchesAsync(
        string stackId,
        CancellationToken cancellationToken = default)
    {
        var stack = await EnsureModuleInstalledAsync(stackId, cancellationToken);
        var stackRoot = GetStackRoot(stackId);
        var settings = await LoadSettingsAsync(stackRoot, cancellationToken);
        if (!settings.Bootstrapped)
        {
            throw new InvalidOperationException("Prepare server-wide progression before running patch validation.");
        }

        var errors = new List<string>();
        var keyChecks = new List<IndividualProgressionKeyCheckDto>();
        var patchCount = CountProgressionPatches(stackRoot);
        if (patchCount != IndividualProgressionPatchCatalog.ExpectedPatchCount)
        {
            errors.Add(
                $"Expected {IndividualProgressionPatchCatalog.ExpectedPatchCount} Individual Progression patches, found {patchCount}.");
        }

        foreach (var definition in IndividualProgressionPatchCatalog.All)
        {
            if (ProgressionPatchExists(stackRoot, definition))
            {
                continue;
            }

            if (!PatchIndex.TryParse(definition.Index, out var index, explicitSub1: true))
            {
                errors.Add($"Invalid patch index in catalog: {definition.Index}");
                continue;
            }

            errors.Add($"Missing progression patch folder: {PatchFolderNames.Format(index, definition.Slug)}");
        }

        await DiscoverKeysAsync(stackId, settings, cancellationToken);
        await RefreshValuesAsync(stackId, settings, cancellationToken);

        var modulePath = settings.ModuleConfPath;
        var moduleContent = await ReadConfigContentAsync(stackId, modulePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(moduleContent))
        {
            errors.Add($"Module config not found or empty: {modulePath}. Rebuild the server first.");
        }

        foreach (var key in new[]
                 {
                     settings.Keys.StartingProgression,
                     settings.Keys.ProgressionLimit,
                     settings.Keys.TbcRacesUnlockProgression,
                     settings.Keys.TbcRacesStartingProgression,
                 })
        {
            keyChecks.Add(await ValidateConfigKeyAsync(stackId, modulePath, moduleContent, key, errors, cancellationToken));
            moduleContent = await ReadConfigContentAsync(stackId, modulePath, cancellationToken);
        }

        var worldPath = settings.WorldserverConfPath;
        var worldContent = await ReadConfigContentAsync(stackId, worldPath, cancellationToken);
        keyChecks.Add(await ValidateConfigKeyAsync(
            stackId, worldPath, worldContent, settings.ExpansionKey, errors, cancellationToken));

        var buildFingerprint = IndividualProgressionBuildFingerprint.Compute(stack);
        var passed = errors.Count == 0 && keyChecks.All(check => check.Exists && check.CanRead && check.CanUpdate);
        if (passed && buildFingerprint is null)
        {
            passed = false;
            errors.Add("Server build fingerprint is unavailable. Rebuild the server, then run validation again.");
        }

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

        return new IndividualProgressionValidationResultDto
        {
            Passed = passed,
            IsCurrent = passed && IndividualProgressionBuildFingerprint.IsCurrent(settings, stack),
            ValidatedAt = settings.ValidationPassedAt,
            BuildFingerprint = settings.ValidationBuildFingerprint,
            PatchCount = patchCount,
            ExpectedPatchCount = IndividualProgressionPatchCatalog.ExpectedPatchCount,
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

        if (IndividualProgressionBuildFingerprint.IsCurrent(settings, stack))
        {
            return (true, null);
        }

        return (false,
            "Individual Progression patch validation is required. Import your patch content, rebuild the server if needed, then click Perform patch validation check.");
    }

    private static bool ProgressionPatchExists(string stackRoot, ProgressionPatchDefinition definition)
    {
        if (!PatchIndex.TryParse(definition.Index, out var index, explicitSub1: true))
        {
            return false;
        }

        var patchKey = PatchFolderNames.Format(index, definition.Slug);
        var metadataPath = Path.Combine(MigrationLayout.PatchDir(stackRoot, patchKey), ProgressionMetadataFileName);
        return File.Exists(metadataPath);
    }

    private async Task<IndividualProgressionKeyCheckDto> ValidateConfigKeyAsync(
        string stackId,
        string configPath,
        string content,
        string key,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        var check = new IndividualProgressionKeyCheckDto
        {
            Key = key,
            ConfigPath = configPath,
        };

        if (string.IsNullOrWhiteSpace(content))
        {
            check.Error = "Config file is empty or missing.";
            errors.Add($"{key}: config file {configPath} is empty or missing.");
            return check;
        }

        if (!ServerConfigValueEditor.TryGetValue(content, key, out var value))
        {
            check.Error = "Key not found in config.";
            errors.Add($"{key}: not found in {configPath}.");
            return check;
        }

        check.Exists = true;
        check.CanRead = true;
        check.Value = value;

        try
        {
            var updated = ServerConfigValueEditor.SetValue(content, key, value);
            if (!ServerConfigValueEditor.TryGetValue(updated, key, out var roundTrip) || roundTrip != value)
            {
                check.Error = "Key could not be round-tripped in memory.";
                errors.Add($"{key}: update simulation failed in {configPath}.");
                return check;
            }

            await _serverConfig.SaveAsync(stackId, configPath, updated, cancellationToken);
            var reread = await ReadConfigContentAsync(stackId, configPath, cancellationToken);
            if (!ServerConfigValueEditor.TryGetValue(reread, key, out var afterSave) || afterSave != value)
            {
                check.Error = "Key could not be written back to config.";
                errors.Add($"{key}: could not be updated in {configPath}.");
                return check;
            }

            check.CanUpdate = true;
        }
        catch (Exception ex)
        {
            check.Error = ex.Message;
            errors.Add($"{key}: {ex.Message}");
        }

        return check;
    }

    private static string? ResolveExpansionForPatch(PatchProgressionMetadataDto metadata) => metadata.Expansion switch
    {
        "classic" when metadata.State == 0 => "0",
        "tbc" when metadata.State == 8 => "1",
        "wotlk" when metadata.State == 14 => "2",
        _ => null,
    };

    private static string IncrementConfigValue(IndividualProgressionSettingsDto settings, string key)
    {
        var current = settings.Values.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed) ? parsed : 0;
        return (current + 1).ToString();
    }

    private int SeedProgressionPatches(string stackRoot, bool onlyMissing)
    {
        if (!onlyMissing)
        {
            RemovePlaceholderPatches(stackRoot);
        }

        var definitions = IndividualProgressionPatchCatalog.ResolveDefinitions(stackRoot);
        var created = 0;

        foreach (var definition in definitions)
        {
            if (!PatchIndex.TryParse(definition.Index, out var index, explicitSub1: true))
            {
                continue;
            }

            if (onlyMissing && ProgressionPatchExists(stackRoot, definition))
            {
                continue;
            }

            var patchKey = PatchFolderNames.Format(index, definition.Slug);
            var patchDir = MigrationLayout.PatchDir(stackRoot, patchKey);
            var alreadyExists = Directory.Exists(patchDir);
            MigrationLayout.EnsurePatchDirectories(stackRoot, patchKey);

            var description = $"""
                # {definition.Title}

                **Progression state:** `PROGRESSION_{definition.Slug}` ({definition.State})
                **Expansion:** {definition.Expansion} · **Patch index:** {definition.Index}

                {definition.Description}

                ## Content
                - SQL: import from release archive into `sql/world`, `sql/auth`, or `sql/characters`
                - Client: import MPQ release into `mpq/`
                """;

            File.WriteAllText(Path.Combine(patchDir, "description.md"), description);

            var metadata = new PatchProgressionMetadataDto
            {
                State = definition.State,
                Slug = definition.Slug,
                Expansion = definition.Expansion,
                IncrementsProgression = definition.IncrementsProgression,
            };
            File.WriteAllText(
                Path.Combine(patchDir, ProgressionMetadataFileName),
                JsonSerializer.Serialize(metadata, JsonOptions));

            if (!alreadyExists || !onlyMissing)
            {
                created++;
            }
        }

        return created;
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
        IndividualProgressionSettingsDto settings,
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
        IndividualProgressionSettingsDto settings,
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
        IndividualProgressionSettingsDto settings,
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

    private async Task<IndividualProgressionSettingsDto> LoadSettingsAsync(
        string stackRoot,
        CancellationToken cancellationToken)
    {
        var path = SettingsFilePath(stackRoot);
        if (!File.Exists(path))
        {
            return new IndividualProgressionSettingsDto();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<IndividualProgressionSettingsDto>(stream, JsonOptions, cancellationToken)
            ?? new IndividualProgressionSettingsDto();
    }

    private static Task PersistSettingsAsync(
        string stackRoot,
        IndividualProgressionSettingsDto settings,
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
                $"Stack must have {IIndividualProgressionSyncService.ModuleId} installed to use Individual Progression features.");
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
}

namespace AzerothPlatform.Core.Contracts;

/// <summary>Per-stack Server Wide Progression orchestration settings (persisted as JSON on disk).</summary>
public sealed class ServerWideProgressionSettingsDto
{
    public bool Bootstrapped { get; set; }

    /// <summary>Build fingerprint the last successful patch validation was run against.</summary>
    public string? ValidationBuildFingerprint { get; set; }

    /// <summary>When patch validation last passed (UTC).</summary>
    public DateTimeOffset? ValidationPassedAt { get; set; }

    public string ModuleConfPath { get; set; } = "modules/individual_progression.conf";

    public string WorldserverConfPath { get; set; } = "worldserver.conf";

    public string ExpansionKey { get; set; } = "Expansion";

    public ServerWideProgressionKeyMappingDto Keys { get; set; } = new();

    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Maps to <c>IndividualProgression.*</c> keys in the module conf.</summary>
public sealed class ServerWideProgressionKeyMappingDto
{
    public string StartingProgression { get; set; } = "IndividualProgression.StartingProgression";

    public string ProgressionLimit { get; set; } = "IndividualProgression.ProgressionLimit";

    public string TbcRacesUnlockProgression { get; set; } = "IndividualProgression.TbcRacesUnlockProgression";

    public string TbcRacesStartingProgression { get; set; } = "IndividualProgression.TbcRacesStartingProgression";
}

public sealed class ServerWideProgressionBootstrapResultDto
{
    public int TemplatesCreated { get; set; }

    public bool ConfigUpdated { get; set; }

    public int Expansion { get; set; }

    public bool KeysDiscovered { get; set; }

    public ServerWideProgressionSettingsDto Settings { get; set; } = new();
}

public sealed class ServerWideProgressionRecreatePatchesResultDto
{
    public int TemplatesCreated { get; set; }

    public int MissingBefore { get; set; }
}

public sealed class ServerWideProgressionKeyCheckDto
{
    public string Key { get; set; } = string.Empty;

    public string ConfigPath { get; set; } = string.Empty;

    /// <summary>Stack patch folder when the check comes from a patch config override.</summary>
    public string? PatchKey { get; set; }

    /// <summary>Patch-local JSON source such as <c>config/worldserver.json</c>.</summary>
    public string? ConfigSource { get; set; }

    public bool Exists { get; set; }

    public bool CanRead { get; set; }

    public bool CanUpdate { get; set; }

    public string? Value { get; set; }

    public string? Error { get; set; }
}

public sealed class ServerWideProgressionValidationResultDto
{
    public bool Passed { get; set; }

    public bool IsCurrent { get; set; }

    public PatchValidationMode Mode { get; set; }

    public DateTimeOffset? ValidatedAt { get; set; }

    public string? BuildFingerprint { get; set; }

    public int PatchCount { get; set; }

    public int ExpectedPatchCount { get; set; }

    public IReadOnlyList<string> Errors { get; set; } = [];

    public IReadOnlyList<ServerWideProgressionKeyCheckDto> KeyChecks { get; set; } = [];
}

/// <summary>A single mapping rule from the progression mapping.json file.</summary>
public sealed class ProgressionSyncMappingEntryDto
{
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public bool Optional { get; set; }
}

/// <summary>Root structure of the mapping.json file defining source-to-destination file mappings.</summary>
public sealed class ProgressionSyncMappingDto
{
    public List<ProgressionSyncMappingEntryDto> Mappings { get; set; } = new();
}

/// <summary>Tracks a user's decision on a single optional file from progression sync.</summary>
public sealed class ProgressionOptionalFileEntryDto
{
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    /// <summary>Whether the user accepted (true) or ignored (false) this optional file.</summary>
    public bool Accepted { get; set; }

    public DateTimeOffset DecidedAt { get; set; }
}

/// <summary>Persisted log of all optional file decisions from progression sync.</summary>
public sealed class ProgressionOptionalFilesLogDto
{
    public List<ProgressionOptionalFileEntryDto> Entries { get; set; } = new();
    public DateTimeOffset LastSyncAt { get; set; }

    /// <summary>Expected progression patch keys from the last successful sync (repo layout snapshot).</summary>
    public List<string> LastKnownPatchKeys { get; set; } = new();
}

/// <summary>
/// Snapshot of Azeroth-Platform-Progression layout captured during sync so validation can run
/// after the on-stack repository checkout is removed.
/// </summary>
public sealed class ProgressionReferenceManifestDto
{
    public DateTimeOffset CapturedAt { get; set; }

    public List<string> ExpectedPatchKeys { get; set; } = new();

    /// <summary>
    /// Required stack-relative file paths per patch key (for example <c>config/worldserver.json</c>).
    /// </summary>
    public Dictionary<string, List<string>> RequiredFilesByPatchKey { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>An optional file that the user previously ignored and may re-prompt for.</summary>
public sealed class ProgressionIgnoredFileDto
{
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTimeOffset DecidedAt { get; set; }
}

/// <summary>Result of a progression sync operation.</summary>
public sealed class ProgressionSyncResultDto
{
    public int CopiedFiles { get; set; }
    public int SkippedOptional { get; set; }

    /// <summary>Optional files that need user confirmation before being added.</summary>
    public List<ProgressionSyncPendingFileDto> PendingOptionalFiles { get; set; } = new();

    /// <summary>Stack patch keys created from the progression repository during this sync.</summary>
    public List<string> NewlyCreatedPatchKeys { get; set; } = new();

    public bool ReapplyAllRecommended { get; set; }

    public string? ReapplyAllReason { get; set; }

    public List<string> Log { get; set; } = new();
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>An optional file awaiting user confirmation during progression sync.</summary>
public sealed class ProgressionSyncPendingFileDto
{
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
}

/// <summary>Status of an in-progress or completed progression sync update.</summary>
public sealed class ProgressionSyncStatusDto
{
    public bool IsRunning { get; set; }
    public bool HasOptionalFilesLog { get; set; }
    public int IgnoredFilesCount { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>True after the first successful progression sync has completed.</summary>
    public bool HasCompletedInitialSync { get; set; }

    public string? Phase { get; set; }

    public int ProgressPercent { get; set; }

    public string? Message { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Error { get; set; }
    public List<string> Log { get; set; } = new();
}

/// <summary>Request to resolve pending optional files.</summary>
public sealed class ResolveOptionalFilesRequest
{
    /// <summary>Map of source file path to accepted (true) or ignored (false).</summary>
    public Dictionary<string, bool> Decisions { get; set; } = new();
}

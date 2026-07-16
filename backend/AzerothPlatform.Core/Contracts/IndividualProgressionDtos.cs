namespace AzerothPlatform.Core.Contracts;

/// <summary>Per-stack Individual Progression orchestration settings (persisted as JSON on disk).</summary>
public sealed class IndividualProgressionSettingsDto
{
    public bool Bootstrapped { get; set; }

    /// <summary>Build fingerprint the last successful patch validation was run against.</summary>
    public string? ValidationBuildFingerprint { get; set; }

    /// <summary>When patch validation last passed (UTC).</summary>
    public DateTimeOffset? ValidationPassedAt { get; set; }

    public string ModuleConfPath { get; set; } = "modules/individual_progression.conf";

    public string WorldserverConfPath { get; set; } = "worldserver.conf";

    public string ExpansionKey { get; set; } = "Expansion";

    public IndividualProgressionKeyMappingDto Keys { get; set; } = new();

    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class IndividualProgressionKeyMappingDto
{
    public string StartingProgression { get; set; } = "IndividualProgression.StartingProgression";

    public string ProgressionLimit { get; set; } = "IndividualProgression.ProgressionLimit";

    public string TbcRacesUnlockProgression { get; set; } = "IndividualProgression.TbcRacesUnlockProgression";

    public string TbcRacesStartingProgression { get; set; } = "IndividualProgression.TbcRacesStartingProgression";
}

public sealed class IndividualProgressionBootstrapResultDto
{
    public int TemplatesCreated { get; set; }

    public bool ConfigUpdated { get; set; }

    public int Expansion { get; set; }

    public bool KeysDiscovered { get; set; }

    public IndividualProgressionSettingsDto Settings { get; set; } = new();
}

public sealed class IndividualProgressionRecreatePatchesResultDto
{
    public int TemplatesCreated { get; set; }

    public int MissingBefore { get; set; }
}

public sealed class PatchProgressionMetadataDto
{
    public int State { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string Expansion { get; set; } = string.Empty;

    public bool IncrementsProgression { get; set; } = true;
}

public sealed class IndividualProgressionKeyCheckDto
{
    public string Key { get; set; } = string.Empty;

    public string ConfigPath { get; set; } = string.Empty;

    public bool Exists { get; set; }

    public bool CanRead { get; set; }

    public bool CanUpdate { get; set; }

    public string? Value { get; set; }

    public string? Error { get; set; }
}

public sealed class IndividualProgressionValidationResultDto
{
    public bool Passed { get; set; }

    public bool IsCurrent { get; set; }

    public DateTimeOffset? ValidatedAt { get; set; }

    public string? BuildFingerprint { get; set; }

    public int PatchCount { get; set; }

    public int ExpectedPatchCount { get; set; }

    public IReadOnlyList<string> Errors { get; set; } = [];

    public IReadOnlyList<IndividualProgressionKeyCheckDto> KeyChecks { get; set; } = [];
}

// ===== Progression Sync (mod-individual-progression + Azeroth-Platform-Progression) =====

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

// ===== MPQ Construction (mpq.json manifest) =====

/// <summary>The mpq.json manifest defining MPQ construction/removal rules for a patch.</summary>
public sealed class MpqManifestDto
{
    /// <summary>MPQ files to be constructed from raw content within the MPQ directory.</summary>
    public List<string> Add { get; set; } = new();

    /// <summary>MPQ files to be removed from the client overlay when the patch is applied.</summary>
    public List<string> Remove { get; set; } = new();

    /// <summary>Human-readable descriptions for each constructed MPQ.</summary>
    public Dictionary<string, string> Description { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Resolved MPQ construction plan across all applied patches.</summary>
public sealed class MpqConstructionPlanDto
{
    /// <summary>MPQ files that need to be constructed (survived all add/remove resolutions).</summary>
    public List<MpqConstructionEntryDto> ToBuild { get; set; } = new();

    /// <summary>MPQ files that were skipped because a later patch removes them.</summary>
    public List<string> Skipped { get; set; } = new();
}

/// <summary>A single MPQ file to be constructed from raw content.</summary>
public sealed class MpqConstructionEntryDto
{
    public string MpqName { get; set; } = string.Empty;
    public string PatchKey { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>True when a pre-built .mpq already exists (skip construction).</summary>
    public bool PreBuilt { get; set; }
}

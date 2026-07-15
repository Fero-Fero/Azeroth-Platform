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

public sealed class MergePatchImportResultDto
{
    public string TargetPatchKey { get; set; } = string.Empty;

    public int SqlFiles { get; set; }

    public int MpqFiles { get; set; }

    public int DbcFiles { get; set; }

    public int MapFiles { get; set; }
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

public sealed class IndividualProgressionReleaseEntryOptions
{
    public int State { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string? SqlUrl { get; set; }

    public string? MpqUrl { get; set; }
}

public sealed class IndividualProgressionReleaseOptions
{
    public const string SectionName = "IndividualProgressionReleases";

    public List<IndividualProgressionReleaseEntryOptions> Patches { get; set; } = new();
}

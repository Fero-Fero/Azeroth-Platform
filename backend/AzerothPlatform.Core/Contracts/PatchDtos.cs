namespace AzerothPlatform.Core.Contracts;

/// <summary>Application status of a patch relative to the stack's current level.</summary>
public enum PatchStatus
{
    /// <summary>Already applied (level &lt;= current applied level).</summary>
    Applied = 0,

    /// <summary>The next patch that can be applied (lowest level greater than current).</summary>
    Next = 1,

    /// <summary>Not yet applicable; a lower patch must be applied first.</summary>
    Locked = 2
}

/// <summary>A single file inside a patch.</summary>
public sealed class PatchFileDto
{
    /// <summary>Category: "sql/world", "sql/auth", "sql/characters", "dbc", "map", "mpq", "config", or "lua".</summary>
    public string Category { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>Author-supplied description of an MPQ's contents (mpq category only; null otherwise).</summary>
    public string? Description { get; set; }
}

/// <summary>Summary of a patch for the overview list.</summary>
public sealed class PatchSummaryDto
{
    /// <summary>Folder name, e.g. "patch 1.1 my_content".</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Semantic patch index string, e.g. "1.1".</summary>
    public string Index { get; set; } = string.Empty;

    /// <summary>Encoded index used for ordering / persistence (internal).</summary>
    public int Level { get; set; }

    /// <summary>Optional label from the folder name after the index.</summary>
    public string Name { get; set; } = string.Empty;

    public PatchStatus Status { get; set; }

    public int SqlCount { get; set; }
    public int DbcCount { get; set; }
    public int MapCount { get; set; }
    public int MpqCount { get; set; }

    /// <summary>From description.md / description.txt in the patch folder, or a default placeholder.</summary>
    public string Description { get; set; } = string.Empty;

    public DateTime? AppliedAt { get; set; }

    public int? ProgressionState { get; set; }

    public string? ProgressionSlug { get; set; }

    /// <summary>Human-readable progression tier title from the IP catalog, when this is a progression patch.</summary>
    public string? ProgressionTitle { get; set; }

    public bool? IncrementsProgression { get; set; }
}

/// <summary>Detailed patch view including its file listing.</summary>
public sealed class PatchDetailsDto
{
    public string Key { get; set; } = string.Empty;
    public string Index { get; set; } = string.Empty;
    public int Level { get; set; }
    public string Name { get; set; } = string.Empty;
    public PatchStatus Status { get; set; }
    public DateTime? AppliedAt { get; set; }

    /// <summary>From description.md / description.txt in the patch folder, or a default placeholder.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Which description file is stored on disk, when one exists.</summary>
    public string? DescriptionFile { get; set; }

    public List<PatchFileDto> Files { get; set; } = new();

    /// <summary>
    /// Names of already-published client MPQ files this patch removes from the client overlay when
    /// applied (removed before any new MPQs in this patch are published).
    /// </summary>
    public List<string> MpqRemovals { get; set; } = new();

    public PatchProgressionMetadataDto? Progression { get; set; }

    /// <summary>
    /// Parsed config overrides from <c>config/*.json</c> that will be applied to server <c>.conf</c> files.
    /// </summary>
    public List<PatchConfigOverrideDto> ConfigOverrides { get; set; } = new();

    /// <summary>Whether this patch folder contains a <c>news/article.json</c> player-facing article.</summary>
    public bool HasPatchNews { get; set; }

    /// <summary>Headline from <c>news/article.json</c> when present.</summary>
    public string? PatchNewsTitle { get; set; }
}

/// <summary>Preview of a patch-authored launcher news article before apply.</summary>
public sealed class PatchNewsPreviewDto
{
    public bool Available { get; set; }

    public string? Error { get; set; }

    public string? Id { get; set; }

    public string? Title { get; set; }

    public string? Date { get; set; }

    public string? Tag { get; set; }

    public string? Html { get; set; }

    public bool HasCover { get; set; }

    /// <summary>Relative API URL to the patch news cover image for preview.</summary>
    public string? CoverUrl { get; set; }
}

/// <summary>A single key/value override from a patch config JSON file.</summary>
public sealed class PatchConfigOverrideDto
{
    public string SourceJson { get; set; } = string.Empty;

    public string TargetConf { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    /// <summary>Populated when previewing against live server configs.</summary>
    public bool ConfFound { get; set; }

    /// <summary>Populated when previewing against live server configs.</summary>
    public bool KeyFound { get; set; }

    /// <summary>Current value from the live server config, when available.</summary>
    public string? CurrentValue { get; set; }
}

/// <summary>A client MPQ file currently published to a stack's client overlay (served to players).</summary>
public sealed class PublishedMpqDto
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }

    /// <summary>True for the reserved, auto-generated patch-D.MPQ (built from DBC content).</summary>
    public bool IsReserved { get; set; }
}

/// <summary>Request to set the list of published MPQ files a patch removes on apply.</summary>
public sealed class SetMpqRemovalsRequest
{
    public List<string> FileNames { get; set; } = new();
}

/// <summary>Overview of a stack's migration state.</summary>
public sealed class MigrationOverviewDto
{
    public string StackId { get; set; } = string.Empty;

    /// <summary>Highest applied patch index encoded as an int (0 = none).</summary>
    public int CurrentLevel { get; set; }

    /// <summary>Highest applied patch index string (empty when none).</summary>
    public string CurrentIndex { get; set; } = string.Empty;

    /// <summary>Whether the server_dbc baseline has been captured (required for DBC patches).</summary>
    public bool BaselineInitialized { get; set; }

    /// <summary>True when an apply/reapply is currently running for this stack (cross-user lock held).</summary>
    public bool IsApplying { get; set; }

    /// <summary>Key of the patch currently being applied ("*" for reapply-all), or null when idle.</summary>
    public string? ApplyingPatchKey { get; set; }

    public List<PatchSummaryDto> Patches { get; set; } = new();

    public bool HasIndividualProgressionModule { get; set; }

    public bool IndividualProgressionBootstrapped { get; set; }

    /// <summary>True when IP is bootstrapped and patch apply requires a validation check first.</summary>
    public bool IndividualProgressionValidationRequired { get; set; }

    /// <summary>True when validation passed for the current server build fingerprint.</summary>
    public bool IndividualProgressionValidationCurrent { get; set; }

    public DateTimeOffset? IndividualProgressionValidationPassedAt { get; set; }

    public int IndividualProgressionPatchCount { get; set; }

    public int IndividualProgressionExpectedPatchCount { get; set; }
}

/// <summary>Live status of a background apply/reapply run, returned by the status-poll endpoint.</summary>
public sealed class ApplyStatusDto
{
    /// <summary>Whether a run is currently in progress.</summary>
    public bool IsApplying { get; set; }

    /// <summary>Key of the patch being applied ("*" for reapply-all), or null when idle.</summary>
    public string? PatchKey { get; set; }

    /// <summary>Identifier of the current/last run; used to download its log.</summary>
    public string? RunId { get; set; }

    /// <summary>Human-readable current stage (e.g. "sql", "dbc", "build-patch-d").</summary>
    public string? Phase { get; set; }

    /// <summary>Trace/correlation id matching the server logs.</summary>
    public string? CorrelationId { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Null while running; true/false once the run finished.</summary>
    public bool? Success { get; set; }

    public string? Error { get; set; }

    /// <summary>Accumulated log lines for the current/last run.</summary>
    public List<string> Log { get; set; } = new();

    /// <summary>Whether a downloadable trace-log file exists for the current/last run.</summary>
    public bool LogAvailable { get; set; }
}

/// <summary>Result of applying a patch.</summary>
public sealed class ApplyPatchResultDto
{
    public bool Success { get; set; }
    public string PatchKey { get; set; } = string.Empty;
    public int Level { get; set; }
    public List<string> Log { get; set; } = new();
    public string? Error { get; set; }

    /// <summary>
    /// Trace/correlation id for this run; matches the <c>TraceId</c> in the server logs so an
    /// operator can find the full trace for a given apply.
    /// </summary>
    public string? CorrelationId { get; set; }
}

/// <summary>Request to create a new patch folder.</summary>
public sealed class CreatePatchRequest
{
    /// <summary>Expansion the patch belongs to: "classic", "tbc", or "wotlk".</summary>
    public string Expansion { get; set; } = string.Empty;

    /// <summary>
    /// Index tier: "expansion" (root only, e.g. 1), "patch" (release, e.g. 1.1), or "hotfix" (e.g. 1.1.1).
    /// Defaults to "patch".
    /// </summary>
    public string Kind { get; set; } = "patch";

    /// <summary>Optional label appended to the folder name after the index.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Parent patch index (e.g. "1.2") when <see cref="Kind"/> is "hotfix".</summary>
    public string? ParentIndex { get; set; }
}

/// <summary>A single patch folder imported from a patch collection archive.</summary>
public sealed class ImportedPatchDto
{
    public string Expansion { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public string TargetKey { get; set; } = string.Empty;
}

/// <summary>Result of importing a patch collection archive.</summary>
public sealed class ImportPatchCollectionResultDto
{
    public string Mode { get; set; } = string.Empty;
    public int ImportedCount { get; set; }
    public List<ImportedPatchDto> ImportedPatches { get; set; } = new();
}

/// <summary>Request to save a patch-level description.</summary>
public sealed class SavePatchDescriptionRequest
{
    public string Content { get; set; } = string.Empty;
}

namespace AzerothPlatform.Infrastructure.Data.Entities;

/// <summary>
/// A point-in-time snapshot of a stack's databases, configuration, and (for pre-update) Docker
/// image tags, taken before an update (or manually) so the operator can roll back if an update
/// breaks something. The dump files live on disk under <c>{stackRoot}/revisions/{Id}/</c>; this
/// row is the index/metadata.
/// </summary>
public class StackRevisionEntity
{
    public string Id { get; set; } = string.Empty;

    public string StackId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>Why the snapshot was taken: "pre-update" or "manual".</summary>
    public string Reason { get; set; } = "manual";

    /// <summary>Lifecycle of the snapshot: "creating", "ready", or "failed".</summary>
    public string Status { get; set; } = "creating";

    /// <summary>Optional error message when <see cref="Status"/> is "failed".</summary>
    public string? Error { get; set; }

    // ===== Metadata captured at snapshot time (for reference / rebuild) =====
    public string CoreCommitSha { get; set; } = string.Empty;

    public string ModuleVersionsJson { get; set; } = "[]";

    public int AppliedPatchLevel { get; set; }

    public string AppliedPatchesJson { get; set; } = "[]";

    /// <summary>Total size on disk of the snapshot's dump files, in bytes.</summary>
    public long SizeBytes { get; set; }
}

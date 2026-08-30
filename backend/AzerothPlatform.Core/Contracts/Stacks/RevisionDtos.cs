namespace AzerothPlatform.Core.Contracts;

/// <summary>A stored point-in-time snapshot of a stack's databases, configuration, and optional server images.</summary>
public sealed class RevisionDto
{
    public string Id { get; set; } = string.Empty;

    public string StackId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>"pre-update" or "manual".</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>"creating", "ready", or "failed".</summary>
    public string Status { get; set; } = string.Empty;

    public string? Error { get; set; }

    /// <summary>Core commit SHA at snapshot time. Restore writes this back so Update stack can prompt again.</summary>
    public string CoreCommitSha { get; set; } = string.Empty;

    public int AppliedPatchLevel { get; set; }

    public long SizeBytes { get; set; }
}

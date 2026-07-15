namespace AzerothPlatform.Launcher.Services;

/// <summary>Progress information reported during verify/download.</summary>
public sealed record SyncProgress
{
    public string Status { get; init; } = string.Empty;
    public int FilesCompleted { get; init; }
    public int FilesTotal { get; init; }
    public long BytesCompleted { get; init; }
    public long BytesTotal { get; init; }

    /// <summary>Overall completion from 0 to 1, or null when indeterminate.</summary>
    public double? Fraction { get; init; }
}

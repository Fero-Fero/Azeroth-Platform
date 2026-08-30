namespace AzerothPlatform.Core.Contracts;

/// <summary>Status of the manager-wide vanilla DBC CSV store used as the module-install trim baseline.</summary>
public sealed class DbcBaselineStoreDto
{
    public bool Ready { get; set; }
    public bool InProgress { get; set; }
    public string? Tag { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? SyncedAt { get; set; }
    public int TableCount { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public IReadOnlyList<string> RecentLogs { get; set; } = [];
}

/// <summary>On-disk manifest for <c>dbc-store/</c>.</summary>
public sealed class DbcBaselineManifest
{
    public string? Tag { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? SyncedAt { get; set; }
    public int TableCount { get; set; }
}

namespace AzerothPlatform.Infrastructure.Data.Entities;

public class CloudAuditLogEntity
{
    public string Id { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string ResourceType { get; set; } = string.Empty;

    public string? ResourceId { get; set; }

    public string Summary { get; set; } = string.Empty;

    public string? MetadataJson { get; set; }
}

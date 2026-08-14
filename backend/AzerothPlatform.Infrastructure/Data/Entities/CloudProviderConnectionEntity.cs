namespace AzerothPlatform.Infrastructure.Data.Entities;

public class CloudProviderConnectionEntity
{
    public string Id { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string ProtectedCredentials { get; set; } = string.Empty;

    public string DefaultRegion { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}

namespace AzerothPlatform.Infrastructure.Data.Entities;

public class CloudProviderConnectionEntity
{
    public string Id { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string ProtectedCredentials { get; set; } = string.Empty;

    public string DefaultRegion { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Stored as enum name: Manual, OAuth, or AssumedRole.</summary>
    public string AuthMethod { get; set; } = "Manual";

    public string AccountHint { get; set; } = string.Empty;

    public DateTime? TokenExpiresAtUtc { get; set; }

    public bool NeedsReauth { get; set; }
}

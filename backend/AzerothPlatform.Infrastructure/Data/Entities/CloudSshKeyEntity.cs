namespace AzerothPlatform.Infrastructure.Data.Entities;

/// <summary>Reusable SSH private key stored encrypted for VPC / cloud wizard flows.</summary>
public class CloudSshKeyEntity
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string ProtectedPrivateKey { get; set; } = string.Empty;

    /// <summary>Display-only SHA-256 prefix; never derived from ciphertext alone.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public string DefaultSshUser { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}

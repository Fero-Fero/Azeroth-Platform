namespace AzerothPlatform.Core.Contracts;

public sealed class CloudSshKeyDto
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public string DefaultSshUser { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}

public sealed class CloudSshKeyExportDto
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public string DefaultSshUser { get; set; } = string.Empty;

    public string PrivateKey { get; set; } = string.Empty;
}

public sealed class CreateCloudSshKeyRequestDto
{
    public string Label { get; set; } = string.Empty;

    public string PrivateKey { get; set; } = string.Empty;

    public string DefaultSshUser { get; set; } = string.Empty;
}

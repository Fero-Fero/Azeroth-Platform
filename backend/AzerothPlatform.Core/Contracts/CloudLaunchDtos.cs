namespace AzerothPlatform.Core.Contracts;

public enum CloudLaunchMode
{
    Create = 0,
    BootstrapExisting = 1,
}

public sealed class CloudLaunchRequestDto
{
    /// <summary>Create a new VM (DO/GCP) or bootstrap an existing instance (AWS SSM).</summary>
    public CloudLaunchMode Mode { get; set; } = CloudLaunchMode.Create;

    public string Name { get; set; } = string.Empty;

    public string SshUser { get; set; } = "ubuntu";

    /// <summary>DO region slug, AWS region, or GCP zone/region prefix.</summary>
    public string? Region { get; set; }

    /// <summary>AWS EC2 instance id when <see cref="Mode"/> is BootstrapExisting.</summary>
    public string? InstanceId { get; set; }

    /// <summary>DO size slug or GCP machine type.</summary>
    public string? Size { get; set; }

    /// <summary>DO/GCP image slug or source image path.</summary>
    public string? Image { get; set; }

    /// <summary>Use an existing saved SSH key for the new VM (public key uploaded to provider when needed).</summary>
    public string? SavedSshKeyId { get; set; }

    /// <summary>When true and no saved key is selected, generate a new key pair and store it in the vault.</summary>
    public bool GenerateSshKey { get; set; } = true;
}

public sealed class CloudLaunchResultDto
{
    public CloudInstanceDto Instance { get; set; } = new();

    public string? SavedSshKeyId { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? BootstrapCommandId { get; set; }
}

public sealed class CloudLaunchDefaultsDto
{
    public CloudProvider Provider { get; set; }

    public string Region { get; set; } = string.Empty;

    public string Size { get; set; } = string.Empty;

    public string Image { get; set; } = string.Empty;

    public string SshUser { get; set; } = "ubuntu";

    public bool SupportsCreate { get; set; }

    public bool SupportsBootstrapExisting { get; set; }
}

public sealed class CloudLaunchCatalogOptionDto
{
    public string Value { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string? Description { get; set; }
}

public sealed class CloudLaunchCatalogDto
{
    public CloudProvider Provider { get; set; }

    public IReadOnlyList<CloudLaunchCatalogOptionDto> Regions { get; set; } = [];

    public IReadOnlyList<CloudLaunchCatalogOptionDto> Sizes { get; set; } = [];

    public IReadOnlyList<CloudLaunchCatalogOptionDto> Images { get; set; } = [];
}

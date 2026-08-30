namespace AzerothPlatform.Core.Contracts;

public enum CloudLaunchMode
{
    Create = 0,
    BootstrapExisting = 1,
}

public sealed class CloudLaunchRequestDto
{
    /// <summary>Create a new VM, or apply bootstrap/firewall on an existing instance.</summary>
    public CloudLaunchMode Mode { get; set; } = CloudLaunchMode.Create;

    public string Name { get; set; } = string.Empty;

    public string SshUser { get; set; } = VpcBootstrapUserData.DefaultOperatorUser;

    public RemoteHostOs TargetOs { get; set; } = RemoteHostOs.Linux;

    /// <summary>DO region slug, AWS region, or GCP zone/region prefix.</summary>
    public string? Region { get; set; }

    /// <summary>AWS EC2 instance id when <see cref="Mode"/> is BootstrapExisting.</summary>
    public string? InstanceId { get; set; }

    /// <summary>DO size slug or GCP machine type.</summary>
    public string? Size { get; set; }

    /// <summary>
    /// Root disk size in GB when the provider lets the volume be sized independently of the
    /// instance SKU (AWS EBS, GCP boot disk). Ignored when the catalog does not support it.
    /// </summary>
    public int? DiskSizeGb { get; set; }

    /// <summary>DO/GCP image slug or source image path.</summary>
    public string? Image { get; set; }

    /// <summary>Use an existing saved SSH key for the new VM (public key uploaded to provider when needed).</summary>
    public string? SavedSshKeyId { get; set; }

    /// <summary>When true and no saved key is selected, generate a new key pair and store it in the vault.</summary>
    public bool GenerateSshKey { get; set; } = true;

    /// <summary>Apply the default cloud security group profile (SSH + player/web; not MySQL/SOAP).</summary>
    public bool ApplyNetworkProfile { get; set; } = true;

    /// <summary>
    /// Admin SSH source CIDR (for example 203.0.113.10/32). Empty lets the API use the request
    /// client IP; if that is also unknown, launch allows SSH from anywhere.
    /// </summary>
    public string? AdminSourceCidr { get; set; }
}

public sealed class CloudLaunchResultDto
{
    public CloudInstanceDto Instance { get; set; } = new();

    public string? SavedSshKeyId { get; set; }

    /// <summary>Vault id used to SSH as ubuntu/root during Verify VPC. Deleted after that lock.</summary>
    public string? BootstrapSshKeyId { get; set; }

    /// <summary>Image-default SSH user for the bootstrap key (ubuntu or root). Used as the .pem filename.</summary>
    public string? BootstrapSshUser { get; set; }

    /// <summary>Operator PEM, only when this launch generated a new azp-admin key. Download immediately.</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>Bootstrap PEM for ubuntu/root. Download immediately as {BootstrapSshUser}.pem.</summary>
    public string? BootstrapPrivateKeyPem { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? BootstrapCommandId { get; set; }
}

public sealed class CloudFirewallProbeRequestDto
{
    public string PublicHost { get; set; } = string.Empty;

    public string? Region { get; set; }

    public string? InstanceId { get; set; }

    public string? AdminSourceCidr { get; set; }
}

public sealed class CloudFirewallProbeResultDto
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public List<RemotePrerequisiteCheckDto> Checks { get; set; } = [];
}

public sealed class CloudLaunchDefaultsDto
{
    public CloudProvider Provider { get; set; }

    public string Region { get; set; } = string.Empty;

    public string Size { get; set; } = string.Empty;

    public int? DiskSizeGb { get; set; }

    public bool SupportsCustomDiskSize { get; set; }

    public string Image { get; set; } = string.Empty;

    public string SshUser { get; set; } = VpcBootstrapUserData.DefaultOperatorUser;

    public bool SupportsCreate { get; set; }

    public bool SupportsBootstrapExisting { get; set; }

    public bool SupportsWindowsLaunch { get; set; }

    public RemoteHostOs TargetOs { get; set; } = RemoteHostOs.Linux;
}

public sealed class CloudLaunchCatalogOptionDto
{
    public string Value { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>vCPU count when the option is an instance size / machine type.</summary>
    public int? Vcpus { get; set; }

    /// <summary>Included local disk in GB when the SKU bundles storage (DO, Hetzner, Vultr).</summary>
    public int? DiskGb { get; set; }
}

public sealed class CloudLaunchCatalogDto
{
    public CloudProvider Provider { get; set; }

    public IReadOnlyList<CloudLaunchCatalogOptionDto> Regions { get; set; } = [];

    public IReadOnlyList<CloudLaunchCatalogOptionDto> Sizes { get; set; } = [];

    public IReadOnlyList<CloudLaunchCatalogOptionDto> Images { get; set; } = [];

    /// <summary>True when create can set root disk size independently of the size SKU (AWS, GCP).</summary>
    public bool SupportsCustomDiskSize { get; set; }

    public int DefaultDiskSizeGb { get; set; }

    public int MinDiskSizeGb { get; set; }

    public int MaxDiskSizeGb { get; set; }
}

/// <summary>Root-disk bounds for providers that accept a custom volume size at launch.</summary>
public static class CloudLaunchStorage
{
    public const int MaxDiskSizeGb = 1000;

    public static int DefaultDiskSizeGb(bool windows) => windows ? 80 : 40;

    public static int MinDiskSizeGb(bool windows) => windows ? 50 : 20;

    public static int ClampDiskSizeGb(int? requested, bool windows)
    {
        var value = requested ?? DefaultDiskSizeGb(windows);
        return Math.Clamp(value, MinDiskSizeGb(windows), MaxDiskSizeGb);
    }
}

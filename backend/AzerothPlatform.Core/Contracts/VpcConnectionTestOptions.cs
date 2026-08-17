namespace AzerothPlatform.Core.Contracts;

/// <summary>Optional Verify VPC flags for the two-step ubuntu/root then azp-admin SSH flow.</summary>
public sealed class VpcConnectionTestOptions
{
    /// <summary>Private key for ubuntu/root bootstrap. When empty, the operator key is tried as a fallback.</summary>
    public string? BootstrapPrivateKey { get; set; }

    /// <summary>Skip ubuntu/root login because that access was already locked on a previous Verify.</summary>
    public bool BootstrapUserSecured { get; set; }

    /// <summary>Vault id to delete after the bootstrap key is removed from the VM.</summary>
    public string? BootstrapSshKeyId { get; set; }

    /// <summary>Keep EC2 Instance Connect as ubuntu after locking static keys (AWS only).</summary>
    public bool EnableAwsInstanceConnect { get; set; }

    /// <summary>Skip Linux ubuntu/root lock and ufw/sudo checks when the host is Windows.</summary>
    public RemoteHostOs RemoteOs { get; set; } = RemoteHostOs.Linux;
}

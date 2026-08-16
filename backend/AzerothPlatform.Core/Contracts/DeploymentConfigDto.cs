namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Deployment target for a stack: run locally, or build locally and run on a remote Docker host
/// reached over SSH.
/// </summary>
public class DeploymentConfigDto
{
    /// <summary>Where the stack's containers run (default: Local).</summary>
    public DeploymentTarget Target { get; set; } = DeploymentTarget.Local;

    /// <summary>Remote host (IP or DNS) for the external Docker engine. Required when Target is External.</summary>
    public string ExternalHost { get; set; } = string.Empty;

    /// <summary>SSH port on the remote host (default: 22).</summary>
    public int ExternalSshPort { get; set; } = 22;

    /// <summary>SSH username used to connect to the remote host.</summary>
    public string ExternalSshUser { get; set; } = string.Empty;

    /// <summary>
    /// PEM-encoded SSH private key used to authenticate to the remote host. Stored in the platform
    /// database at rest (encrypted via <see cref="ISecretProtector"/>). Leave empty when
    /// <see cref="SavedSshKeyId"/> is set.
    /// </summary>
    public string ExternalSshPrivateKey { get; set; } = string.Empty;

    /// <summary>When set, the platform loads the private key from the SSH key vault (never returned to clients).</summary>
    public string SavedSshKeyId { get; set; } = string.Empty;

    /// <summary>Linked cloud account that launched or selected this stack's VM.</summary>
    public string CloudConnectionId { get; set; } = string.Empty;

    /// <summary>Provider instance id used to terminate the VM with the stack.</summary>
    public string CloudInstanceId { get; set; } = string.Empty;

    /// <summary>Provider region or zone of <see cref="CloudInstanceId"/>.</summary>
    public string CloudRegion { get; set; } = string.Empty;

    /// <summary>Cloud provider of the bound VM (Aws, DigitalOcean, …).</summary>
    public string CloudProvider { get; set; } = string.Empty;

    /// <summary>Provider instance type / size (t3.micro, s-2vcpu-2gb, …).</summary>
    public string CloudInstanceType { get; set; } = string.Empty;

    /// <summary>When creating a stack, save a newly pasted key to the vault for reuse.</summary>
    public bool SaveSshKeyToVault { get; set; } = true;

    /// <summary>Label for a new vault entry when <see cref="SaveSshKeyToVault"/> is true.</summary>
    public string SaveSshKeyLabel { get; set; } = string.Empty;
}

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
    /// database at rest (hardening / encryption is a documented follow-up).
    /// </summary>
    public string ExternalSshPrivateKey { get; set; } = string.Empty;
}

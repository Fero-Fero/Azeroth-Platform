namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Result of probing an external Docker host over SSH.
/// </summary>
public class RemoteConnectionTestResultDto
{
    /// <summary>Whether the remote Docker engine responded successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Human-readable status or error message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Remote Docker server version, when the probe succeeded.</summary>
    public string? ServerVersion { get; set; }

    /// <summary>Individual prerequisite checks (SSH, Docker Engine, Docker Compose, …).</summary>
    public List<RemotePrerequisiteCheckDto> Prerequisites { get; set; } = new();

    /// <summary>
    /// True after ubuntu/root bootstrap SSH succeeded and image-default users were locked.
    /// Persist this so a later Verify VPC does not retry root (the bootstrap key is gone).
    /// </summary>
    public bool BootstrapUserSecured { get; set; }

    /// <summary>OS detected on the host over SSH, when the probe could tell Linux from Windows.</summary>
    public RemoteHostOs? DetectedOs { get; set; }
}

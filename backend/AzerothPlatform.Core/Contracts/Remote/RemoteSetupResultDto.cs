namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Result of running first-time provisioning steps on a remote VPC host over SSH.
/// </summary>
public class RemoteSetupResultDto
{
    /// <summary>Whether all required setup steps completed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Human-readable summary.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Remote Docker server version after setup, when available.</summary>
    public string? ServerVersion { get; set; }

    /// <summary>Individual setup steps executed (or skipped) on the remote host.</summary>
    public List<RemotePrerequisiteCheckDto> Steps { get; set; } = new();
}

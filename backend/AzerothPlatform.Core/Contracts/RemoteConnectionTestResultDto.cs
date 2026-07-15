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
}

namespace AzerothPlatform.Core.Contracts;

/// <summary>Result of running the VPC bootstrap script over SSH from the wizard.</summary>
public sealed class RemoteBootstrapResultDto
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>Combined stdout/stderr from the remote script (truncated when very long).</summary>
    public string? Output { get; set; }

    public string? DockerVersion { get; set; }
}

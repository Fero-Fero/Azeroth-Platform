namespace AzerothPlatform.Core.Contracts;

/// <summary>Request body for probing an external Docker host over SSH.</summary>
public class RemoteConnectionTestRequestDto
{
    public DeploymentConfigDto Deployment { get; set; } = new();

    /// <summary>Which phase of the connection test to run. Defaults to full probe.</summary>
    public RemoteConnectionTestPhase Phase { get; set; } = RemoteConnectionTestPhase.Full;
}

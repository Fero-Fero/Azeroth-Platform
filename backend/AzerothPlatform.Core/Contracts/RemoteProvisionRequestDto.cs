namespace AzerothPlatform.Core.Contracts;

/// <summary>SSH credentials plus provisioning options for a remote VPC.</summary>
public class RemoteProvisionRequestDto
{
    public DeploymentConfigDto Deployment { get; set; } = new();

    public RemoteSetupOptionsDto Options { get; set; } = new();
}

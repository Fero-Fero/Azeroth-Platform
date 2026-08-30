using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services.RemoteHost;

/// <summary>
/// Linux host setup stays in <c>RemoteEngineService</c> (apt/systemd/ufw helpers).
/// </summary>
public sealed class LinuxRemoteSetupStrategy : IRemoteHostSetupStrategy
{
    public RemoteHostOs Os => RemoteHostOs.Linux;

    public Task ProbePrerequisitesAsync(
        IRemoteSshSession session,
        List<RemotePrerequisiteCheckDto> checks,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException("Linux Verify VPC checks run in RemoteEngineService.");

    public Task<RemoteSetupResultDto> ProvisionAsync(
        IRemoteSshSession session,
        RemoteSetupOptionsDto options,
        List<RemotePrerequisiteCheckDto> steps,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException("Linux first-time setup runs in RemoteEngineService.");

    public Task<RemoteSetupResultDto?> ApplyFirewallAsync(
        IRemoteSshSession session,
        RemoteSetupOptionsDto options,
        List<RemotePrerequisiteCheckDto> steps,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException("Linux ufw sync runs in RemoteEngineService.");

    public Task ProbeFirewallAsync(
        IRemoteSshSession session,
        VpcSecurityProfileDto profile,
        VpcFirewallStatusDto result,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException("Linux ufw probes run in RemoteEngineService.");
}

using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface ICloudFirewallService
{
    Task<CloudFirewallApplyResultDto> ApplyStackSecurityGroupRulesAsync(
        string stackId,
        SyncCloudSecurityGroupRequestDto request,
        CancellationToken cancellationToken = default);

    Task<CloudFirewallProbeResultDto> ProbeLaunchSecurityGroupAsync(
        string connectionId,
        CloudFirewallProbeRequestDto request,
        CancellationToken cancellationToken = default);
}

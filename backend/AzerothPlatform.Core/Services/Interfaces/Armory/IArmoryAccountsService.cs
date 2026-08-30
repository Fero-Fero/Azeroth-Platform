using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface IArmoryAccountsService
{
    Task<ArmoryAccountsStatusDto> GetStatusAsync(string stackId, CancellationToken cancellationToken = default);

    Task<int> GetPendingRegistrationCountAsync(string stackId, CancellationToken cancellationToken = default);

    Task<ArmoryTestEmailResultDto> SendTestEmailAsync(
        string stackId,
        ArmoryTestEmailRequestDto request,
        CancellationToken cancellationToken = default);
}

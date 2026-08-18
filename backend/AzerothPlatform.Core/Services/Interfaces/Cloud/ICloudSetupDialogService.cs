using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface ICloudSetupDialogService
{
    Task<CloudInstanceSetupDialogDto> GetAsync(
        string connectionId,
        CancellationToken cancellationToken = default);
}

using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

public interface ICloudLaunchService
{
    Task<CloudLaunchDefaultsDto> GetDefaultsAsync(
        string connectionId,
        CancellationToken cancellationToken = default,
        RemoteHostOs targetOs = RemoteHostOs.Linux);

    Task<CloudLaunchCatalogDto> GetCatalogAsync(
        string connectionId,
        string? region = null,
        CancellationToken cancellationToken = default,
        RemoteHostOs targetOs = RemoteHostOs.Linux);

    Task<CloudLaunchResultDto> LaunchAsync(
        string connectionId,
        CloudLaunchRequestDto request,
        CancellationToken cancellationToken = default);
}

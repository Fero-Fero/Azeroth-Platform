using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Data.Entities;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Streams manager-built sidecar tool images (WDBX, MPQ packer) to an external stack's remote engine.
/// </summary>
internal static class SidecarImageShipping
{
    public static async Task ShipToStackEngineIfNeededAsync(
        ManagedStackEntity stack,
        IRemoteEngineService remoteEngine,
        string imageTag,
        CancellationToken cancellationToken = default)
    {
        if (stack.DeploymentTarget != DeploymentTarget.External || string.IsNullOrWhiteSpace(imageTag))
        {
            return;
        }

        await remoteEngine.ShipImageAsync(stack, imageTag, cancellationToken);
    }
}

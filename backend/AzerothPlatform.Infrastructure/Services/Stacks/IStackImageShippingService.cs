using AzerothPlatform.Infrastructure.Data.Entities;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Streams locally-built stack images to an external stack's remote Docker engine so
/// <c>docker --context … compose up</c> finds them without pulling from a registry.
/// </summary>
public interface IStackImageShippingService
{
    /// <summary>
    /// Best-effort ship of AzerothCore stack images plus optional armory/client-server images.
    /// Ensures images exist locally (including <c>localhost/</c> tag aliases) before streaming.
    /// </summary>
    Task ShipStackImagesAsync(
        ManagedStackEntity stack,
        bool includeArmory,
        bool includeClient,
        CancellationToken cancellationToken = default);
}

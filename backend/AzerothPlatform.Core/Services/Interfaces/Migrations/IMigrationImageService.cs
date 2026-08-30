namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Ensures the docker sidecar images used by the patch pipeline exist, building them once from source
/// baked into the manager image and caching thereafter, so an apply only ever <c>docker run</c>s a
/// ready image instead of recompiling.
/// </summary>
public interface IMigrationImageService
{
    /// <summary>Builds the lightweight MPQ packaging image if it is missing (fast, cached afterwards).</summary>
    Task EnsureMpqToolImageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the WDBX editor image if it is missing. The first build is a heavy one-time cost
    /// (Wine + .NET Framework 4.8, ~2-3 GB); it is cached thereafter.
    /// </summary>
    Task EnsureWdbxImageAsync(CancellationToken cancellationToken = default);
}

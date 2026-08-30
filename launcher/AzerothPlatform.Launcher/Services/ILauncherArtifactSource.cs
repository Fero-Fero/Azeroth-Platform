namespace AzerothPlatform.Launcher.Services;

/// <summary>
/// Abstracts where the launcher fetches its self-update artifact from, so <see cref="SelfUpdateService"/>
/// works identically against the manager's admin build endpoints and a stack's own
/// <c>/launcher/*</c> endpoints, which are the default for stack-hosted portals.
/// </summary>
public interface ILauncherArtifactSource
{
    /// <summary>Latest available launcher build: version, its SHA-256 (for integrity), and availability.</summary>
    Task<(string? Version, string? Sha256, bool Available)> GetLatestAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the latest launcher exe to <paramref name="destinationPath"/>.</summary>
    Task DownloadAsync(string destinationPath, CancellationToken cancellationToken);
}

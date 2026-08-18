namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Ensures the shared per-stack client-server (<c>azeroth-platform-client</c>) Docker image has been
/// built on the target daemon so stacks can reference it by tag. Optionally targets a specific docker
/// context (external stacks build/run on the remote engine).
/// </summary>
public interface IClientServerImageService
{
    /// <summary>
    /// Builds the client-server image if it does not already exist on the given docker context
    /// (null = the local/default engine). Safe to call repeatedly; concurrent callers share a build.
    /// </summary>
    Task EnsureImageAsync(string? dockerContext = null, CancellationToken cancellationToken = default);

    /// <summary>Rebuilds the client-server image unconditionally from the current baked source.</summary>
    Task RebuildImageAsync(string? dockerContext = null, CancellationToken cancellationToken = default);
}

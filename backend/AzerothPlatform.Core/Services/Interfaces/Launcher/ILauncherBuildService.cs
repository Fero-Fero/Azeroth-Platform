using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Compiles the desktop launcher once on the manager's local engine (dotnet publish win-x64
/// single-file) with the global identity baked in, then broadcasts the produced exe to every
/// launcher-visible, client-enabled stack's launcher-dist volume so each stack serves it itself.
/// Per-stack branding/realmlist/template overrides are applied at runtime from each stack's portal.
/// </summary>
public interface ILauncherBuildService
{
    /// <summary>Current compile status + info about the currently available built exe.</summary>
    Task<LauncherBuildStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a compile if none is running, bumping the given segment of the launcher's
    /// Release.Update.Minor.Patch version. The launcher is built locally and then pushed to every
    /// launcher-visible, client-enabled stack. Returns the status snapshot after kicking off.
    /// </summary>
    /// <param name="part">Version segment to bump.</param>
    Task<LauncherBuildStatusDto> StartBuildAsync(LauncherVersionPart part, CancellationToken cancellationToken = default);

    /// <summary>Absolute path to the built launcher exe, or null when no build is available.</summary>
    string? GetExecutablePath();

    /// <summary>
    /// Pings every client-enabled stack for the launcher version it currently serves and compares each
    /// against the manager's most recently built version, so the admin can verify the build propagated.
    /// </summary>
    Task<LauncherPropagationDto> GetStackVersionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-pushes the currently built launcher exe (+ build.json) to a single stack's launcher-dist volume
    /// and returns that stack's refreshed version status. Use to repair a stack that missed a build (e.g.
    /// it was offline when the launcher was compiled). Throws when no build is available or the stack is
    /// unknown / has no client container.
    /// </summary>
    Task<LauncherStackVersionDto> ResendToStackAsync(string stackId, CancellationToken cancellationToken = default);
}

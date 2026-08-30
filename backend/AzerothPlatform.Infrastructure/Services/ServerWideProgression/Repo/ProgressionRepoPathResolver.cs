using AzerothPlatform.Infrastructure.Services.Patches;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

/// <summary>
/// Resolves the per-stack Azeroth-Platform-Progression checkout used for sync and validation.
/// </summary>
internal static class ProgressionRepoPathResolver
{
    public static string Resolve(string stackRoot) =>
        Path.GetFullPath(MigrationLayout.ProgressionRepoDir(stackRoot));
}

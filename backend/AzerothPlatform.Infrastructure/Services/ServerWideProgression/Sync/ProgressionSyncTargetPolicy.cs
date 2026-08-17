using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services.Migrations;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

/// <summary>
/// Decides whether progression sync may write into a resolved patch destination.
/// </summary>
public static class ProgressionSyncTargetPolicy
{
    private const string ProgressionMetadataFileName = "progression.json";

    public static bool IsInitialSync(ProgressionOptionalFilesLogDto log) =>
        log.LastSyncAt == default;

    public static bool TryGetPatchKeyFromPath(string stackRoot, string path, out string patchKey)
    {
        patchKey = string.Empty;
        var migrationsRoot = Path.GetFullPath(MigrationLayout.MigrationsRoot(stackRoot));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(migrationsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, migrationsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = Path.GetRelativePath(migrationsRoot, fullPath);
        var segment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (string.IsNullOrEmpty(segment) || segment == ".")
        {
            return false;
        }

        patchKey = segment;
        return true;
    }

    public static bool IsManagedProgressionPatch(string stackRoot, string patchKey) =>
        File.Exists(Path.Combine(MigrationLayout.PatchDir(stackRoot, patchKey), ProgressionMetadataFileName));

    /// <summary>
    /// Initial sync may overwrite any resolved destination. Later syncs only touch managed progression patches.
    /// </summary>
    public static bool ShouldApplySyncToPath(
        string stackRoot,
        string resolvedPath,
        bool initialSync,
        ICollection<string> log)
    {
        if (initialSync)
        {
            return true;
        }

        if (!TryGetPatchKeyFromPath(stackRoot, resolvedPath, out var patchKey))
        {
            log.Add($"Skipped sync target outside migrations: {resolvedPath}");
            return false;
        }

        if (!IsManagedProgressionPatch(stackRoot, patchKey))
        {
            log.Add($"Skipped custom patch '{patchKey}' (not a managed progression patch).");
            return false;
        }

        return true;
    }
}

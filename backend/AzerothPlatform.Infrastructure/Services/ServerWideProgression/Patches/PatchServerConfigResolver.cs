using AzerothPlatform.Infrastructure.Services.Patches;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

/// <summary>
/// Maps patch <c>config/*.json</c> file names to live server <c>.conf</c> paths under the stack etc dir.
/// </summary>
internal static class PatchServerConfigResolver
{
    public static string? ResolveRelativeConfPath(string stackRoot, string jsonBaseName)
    {
        var etcDir = MigrationLayout.EtcDir(stackRoot);
        var absolute = ResolveConfPath(etcDir, jsonBaseName);
        if (absolute is null)
        {
            return null;
        }

        return Path.GetRelativePath(etcDir, absolute).Replace('\\', '/');
    }

    public static string? ResolveConfPath(string etcDir, string baseName)
    {
        var serverPath = Path.Combine(etcDir, $"{baseName}.conf");
        if (File.Exists(serverPath))
        {
            return serverPath;
        }

        var modulesDir = Path.Combine(etcDir, "modules");
        if (!Directory.Exists(modulesDir))
        {
            return null;
        }

        var modulePath = Path.Combine(modulesDir, $"{baseName}.conf");
        if (File.Exists(modulePath))
        {
            return modulePath;
        }

        return Directory.EnumerateFiles(modulesDir, "*.conf", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => string.Equals(
                Path.GetFileNameWithoutExtension(f), baseName, StringComparison.OrdinalIgnoreCase));
    }
}

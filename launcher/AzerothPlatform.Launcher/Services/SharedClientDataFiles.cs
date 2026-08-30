namespace AzerothPlatform.Launcher.Services;

/// <summary>
/// Keep in sync with <c>AzerothPlatform.ClientManifest/SharedClientDataFiles.cs</c>.
/// </summary>
internal static class SharedClientDataFiles
{
    private static readonly HashSet<string> SharedBaseDataMpqFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "common.mpq",
        "common-2.mpq",
        "expansion.mpq",
        "lichking.mpq",
        "patch.mpq",
        "patch-2.mpq",
        "patch-3.mpq",
    };

    public static bool IsSharedBaseDataFile(string relativePath)
    {
        var rel = relativePath.Replace('\\', '/');
        if (!rel.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var underData = rel["Data/".Length..];
        if (underData.Contains('/'))
        {
            return false;
        }

        return SharedBaseDataMpqFileNames.Contains(underData);
    }

    public static bool IsProfileOverlayMpq(string underData) =>
        !IsSharedBaseDataFile($"Data/{underData.Replace('\\', '/')}");
}

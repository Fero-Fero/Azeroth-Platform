namespace AzerothPlatform.ClientContent;

/// <summary>
/// Standard WoW 3.3.5a archives that ship with the base client and are shared across all server
/// profiles. These must never be treated as per-profile overlay content (stashed under
/// <c>Data/{profile}/</c>).
/// </summary>
public static class SharedClientDataFiles
{
    public static readonly IReadOnlyList<string> SharedBaseDataMpqFileNames =
    [
        "common.mpq",
        "common-2.mpq",
        "expansion.mpq",
        "lichking.mpq",
        "patch.mpq",
        "patch-2.mpq",
        "patch-3.mpq",
    ];

    /// <summary>
    /// True for a manifest path that points at a shared base MPQ directly under <c>Data/</c>.
    /// </summary>
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

        return SharedBaseDataMpqFileNames.Contains(underData, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Overlay must never replace the shared base archives (common, patch, patch-2, …).
    /// </summary>
    public static bool MustNotServeFromOverlay(string relativePath)
        => IsSharedBaseDataFile(relativePath);
}

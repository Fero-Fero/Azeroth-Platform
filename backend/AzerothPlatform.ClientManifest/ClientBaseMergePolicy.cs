namespace AzerothPlatform.ClientContent;

/// <summary>
/// Rules for merging a newly uploaded base client into a stack that already has platform-managed
/// content (letter patch MPQs, addons). Stock Blizzard archives are replaced; custom overlay
/// content is left alone.
/// </summary>
public static class ClientBaseMergePolicy
{
    /// <summary>
    /// True when this relative path is platform-managed and must not be overwritten by a base-client
    /// upload (letter patches such as <c>patch-D.MPQ</c>, and <c>Interface/AddOns</c>).
    /// </summary>
    public static bool ShouldPreservePlatformContent(string? relativePath)
    {
        var rel = Normalize(relativePath);
        if (rel.Length == 0)
        {
            return false;
        }

        if (rel.Equals("Interface/AddOns", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith("Interface/AddOns/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsCustomDataPatchMpq(rel);
    }

    /// <summary>
    /// Stock 3.3.5a archives that must not be deleted from the client file browser:
    /// common, common-2, expansion, lichking, patch, patch-2, patch-3.
    /// </summary>
    public static bool IsProtectedStockMpq(string? relativePath)
    {
        var rel = Normalize(relativePath);
        return rel.Length > 0 && SharedClientDataFiles.IsSharedBaseDataFile(rel);
    }

    /// <summary>
    /// True for a letter-patch archive under <c>Data/</c> (<c>patch-A.MPQ</c> … <c>patch-Z.MPQ</c>).
    /// Stock numbered patches (<c>patch-2</c>, <c>patch-3</c>) and other Data MPQs are not included.
    /// </summary>
    public static bool IsCustomDataPatchMpq(string? relativePath)
    {
        var rel = Normalize(relativePath);
        if (!rel.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = rel["Data/".Length..];
        if (name.Contains('/', StringComparison.Ordinal)
            || !name.EndsWith(".mpq", StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith("patch-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // "patch-X.mpq" — a single letter, not patch-2 / patch-3.
        var letter = name["patch-".Length..^".mpq".Length];
        return letter.Length == 1 && char.IsAsciiLetter(letter[0]);
    }

    private static string Normalize(string? relativePath)
        => (relativePath ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
}

namespace AzerothPlatform.Infrastructure.Services.Migrations;

/// <summary>
/// Filters files when packing raw patch MPQ content. Manifest and sidecar files must never
/// end up inside the constructed client archive.
/// </summary>
internal static class MpqPackFilter
{
    internal const string MpqManifestFileName = "mpq.json";

    public static bool ShouldIncludeInConstructedMpq(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        if (fileName.Equals(MpqManifestFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (fileName.Equals("remove.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".remove.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ext = Path.GetExtension(fileName);
        if (ext.Equals(".mpq", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".desc", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static bool IsValidConstructedMpqName(string mpqName) =>
        !string.IsNullOrWhiteSpace(mpqName)
        && mpqName.EndsWith(".mpq", StringComparison.OrdinalIgnoreCase)
        && !mpqName.Equals(MpqManifestFileName, StringComparison.OrdinalIgnoreCase);
}

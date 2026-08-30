using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services.Patches;

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

    /// <summary>
    /// Output archive names from <c>mpq.json</c> <c>add</c>. WoW content folder names
    /// (<c>Interface.MPQ</c>, <c>World.MPQ</c>, …) are never construction targets.
    /// </summary>
    public static IReadOnlyList<string> ConstructedArchiveNames(MpqManifestDto? manifest)
    {
        if (manifest is null)
        {
            return [];
        }

        return manifest.Add
            .Where(IsValidConstructedMpqName)
            .Where(name => !IsWowContentFolderArchive(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True when <paramref name="mpqName"/> is a stock client tree (Interface, World, Sound, …), not a patch archive.</summary>
    public static bool IsWowContentFolderArchive(string mpqName)
    {
        var stem = Path.GetFileNameWithoutExtension(mpqName);
        return WowContentFolderNames.Contains(stem);
    }

    private static readonly HashSet<string> WowContentFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Interface", "World", "Sound", "Data", "Fonts", "Character", "Creature",
        "Item", "Spells", "Textures", "Environments", "Particles", "Shaders",
        "Cameras", "DBC", "DBFilesClient", "Movies", "WTF",
    };

    /// <summary>
    /// Staging subdirectory under the throwaway work dir. The sidecar writes the output
    /// <c>.mpq</c> next to this folder; the name is not an archive path prefix because
    /// <see cref="ConstructedArchiveToolArgs"/> always passes <c>--preserve-paths</c>.
    /// </summary>
    internal const string ConstructedArchiveStageFolderName = "content";

    /// <summary>
    /// Sidecar arguments that pack the staged tree into <paramref name="mpqName"/> with files
    /// at archive root (<c>Interface\</c>, <c>World\</c>, …). Without <c>--preserve-paths</c>
    /// mkmpq prefixes every path with the stage folder name and the 3.3.5a client ignores the patch.
    /// </summary>
    public static string ConstructedArchiveToolArgs(string mpqName) =>
        $"\"{mpqName}\" \"{ConstructedArchiveStageFolderName}\" --preserve-paths";

    /// <summary>
    /// Loose files and folders packed into the archive named in <c>mpq.json</c> <c>add</c>.
    /// Existing <c>.mpq</c> files stay in the mpq folder and are published as-is; they are never
    /// treated as a per-folder archive (so <c>Interface/</c> does not become <c>Interface.MPQ</c>).
    /// A folder named after the constructed archive (e.g. <c>Patch-W/</c> for <c>Patch-W.MPQ</c>)
    /// is the content tree, not an internal path prefix, when it holds every packable file.
    /// </summary>
    public static string ContentDirectoryFor(string mpqDir, string mpqName)
    {
        var wrapperName = Path.GetFileNameWithoutExtension(mpqName);
        if (string.IsNullOrEmpty(wrapperName))
        {
            return mpqDir;
        }

        var wrapper = Path.Combine(mpqDir, wrapperName);
        if (!Directory.Exists(wrapper))
        {
            return mpqDir;
        }

        var packable = EnumeratePackableFiles(mpqDir).ToList();
        if (packable.Count == 0)
        {
            return mpqDir;
        }

        var prefix = wrapper.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return packable.All(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ? wrapper
            : mpqDir;
    }

    public static IEnumerable<string> EnumeratePackableFiles(string contentDir)
    {
        if (!Directory.Exists(contentDir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(contentDir, "*", SearchOption.AllDirectories))
        {
            if (ShouldIncludeInConstructedMpq(file))
            {
                yield return file;
            }
        }
    }

    public static bool HasPackableContent(string contentDir)
        => EnumeratePackableFiles(contentDir).Any();

    /// <summary>
    /// True when loose files or folders exist that should be packed into the <c>add</c> archive,
    /// even if a leftover constructed <c>.mpq</c> is already on disk.
    /// </summary>
    public static bool HasPackableContentFor(string mpqDir, string mpqName)
        => HasPackableContent(ContentDirectoryFor(mpqDir, mpqName));
}

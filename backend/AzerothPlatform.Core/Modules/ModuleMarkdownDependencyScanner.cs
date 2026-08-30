using System.Text.RegularExpressions;

namespace AzerothPlatform.Core.Modules;

/// <summary>
/// Bounded markdown scan: known dependency tokens only. May add apt packages,
/// never remove them, and never invent services (Qdrant, Ollama) as packages.
/// </summary>
public static class ModuleMarkdownDependencyScanner
{
    private static readonly Regex CurlToken = new(
        @"\b(?:libcurl|cURL|curl)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NlohmannToken = new(
        @"nlohmann\s*(?:/|\s+)?json|nlohmann-json",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BundledOnLine = new(
        @"\bbundled\b|no installation needed|header[- ]only",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public const string LibCurlPackage = "libcurl4-openssl-dev";
    public const string NlohmannPackage = "nlohmann-json3-dev";

    /// <summary>
    /// Root-level markdown only (README plus <c>*Dependencies*</c> files). Does not recurse into src/.
    /// </summary>
    public static IReadOnlyList<string> ScanDirectory(string moduleDir)
    {
        if (string.IsNullOrWhiteSpace(moduleDir) || !Directory.Exists(moduleDir))
        {
            return [];
        }

        var packages = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateScanFiles(moduleDir))
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var package in ScanText(text))
            {
                if (seen.Add(package))
                {
                    packages.Add(package);
                }
            }
        }

        return packages;
    }

    public static IReadOnlyList<string> ScanText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var packages = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in markdown.Split('\n'))
        {
            if (CurlToken.IsMatch(line) && seen.Add(LibCurlPackage))
            {
                packages.Add(LibCurlPackage);
            }

            if (NlohmannToken.IsMatch(line)
                && !BundledOnLine.IsMatch(line)
                && seen.Add(NlohmannPackage))
            {
                packages.Add(NlohmannPackage);
            }
        }

        return packages;
    }

    private static IEnumerable<string> EnumerateScanFiles(string moduleDir)
    {
        foreach (var path in Directory.EnumerateFiles(moduleDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }

        foreach (var path in Directory.EnumerateFiles(moduleDir, "*Dependencies*", SearchOption.TopDirectoryOnly))
        {
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return path;
        }
    }
}

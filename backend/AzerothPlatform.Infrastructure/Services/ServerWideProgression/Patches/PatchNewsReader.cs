using System.Text.Json;
using System.Text.RegularExpressions;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services.Migrations;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

/// <summary>
/// Reads patch <c>news/article.json</c>, optional <c>news/article.html</c>, and images from a stack patch folder.
/// </summary>
internal static partial class PatchNewsReader
{
    public const string NewsDirName = "news";
    public const string ArticleFileName = "article.json";
    public const string HtmlFileName = "article.html";
    public const string ImagesSubdir = "images";

    private static readonly string[] CoverFileNames = ["cover.png", "cover.jpg", "cover.jpeg", "cover.webp"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string PatchNewsDir(string stackRoot, string patchKey) =>
        Path.Combine(MigrationLayout.PatchDir(stackRoot, patchKey), NewsDirName);

    public static string ArticlePath(string stackRoot, string patchKey) =>
        Path.Combine(PatchNewsDir(stackRoot, patchKey), ArticleFileName);

    public static string HtmlPath(string stackRoot, string patchKey) =>
        Path.Combine(PatchNewsDir(stackRoot, patchKey), HtmlFileName);

    public static bool HasArticle(string stackRoot, string patchKey) =>
        File.Exists(ArticlePath(stackRoot, patchKey));

    public static bool TryReadArticle(
        string stackRoot,
        string patchKey,
        out LauncherNewsItemDto article,
        out string? coverImagePath,
        out string? error)
    {
        article = new LauncherNewsItemDto();
        coverImagePath = null;
        error = null;

        var articlePath = ArticlePath(stackRoot, patchKey);
        if (!File.Exists(articlePath))
        {
            error = "Patch news article not found.";
            return false;
        }

        PatchNewsArticleFileDto? stored;
        try
        {
            stored = JsonSerializer.Deserialize<PatchNewsArticleFileDto>(File.ReadAllText(articlePath), JsonOptions);
        }
        catch (Exception ex)
        {
            error = $"Failed to parse news/{ArticleFileName}: {ex.Message}";
            return false;
        }

        if (stored is null)
        {
            error = $"news/{ArticleFileName} is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(stored.Id))
        {
            error = $"news/{ArticleFileName} must contain a non-empty \"id\".";
            return false;
        }

        if (string.IsNullOrWhiteSpace(stored.Title))
        {
            error = $"news/{ArticleFileName} must contain a non-empty \"title\".";
            return false;
        }

        if (stored.IsDraft)
        {
            error = $"news/{ArticleFileName} must not set \"isDraft\" to true for patch apply.";
            return false;
        }

        var html = LoadHtmlBody(stackRoot, patchKey, stored);
        if (string.IsNullOrWhiteSpace(html))
        {
            error = $"news/{HtmlFileName} (or \"html\" in {ArticleFileName}) must contain article body content.";
            return false;
        }

        article = new LauncherNewsItemDto
        {
            Id = stored.Id.Trim(),
            Title = stored.Title.Trim(),
            Date = (stored.Date ?? string.Empty).Trim(),
            Html = html,
            Tag = (stored.Tag ?? "patch").Trim(),
            SortOrder = stored.SortOrder,
            IsDraft = false,
        };

        coverImagePath = ResolveCoverImagePath(stackRoot, patchKey);
        return true;
    }

    public static string? ResolveCoverImagePath(string stackRoot, string patchKey)
    {
        var newsDir = PatchNewsDir(stackRoot, patchKey);
        if (!Directory.Exists(newsDir))
        {
            return null;
        }

        foreach (var fileName in CoverFileNames)
        {
            var path = Path.Combine(newsDir, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public static string? ResolveAssetPath(string stackRoot, string patchKey, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Replace('\\', '/').Trim().TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var newsDir = PatchNewsDir(stackRoot, patchKey);
        var fullPath = Path.GetFullPath(Path.Combine(newsDir, normalized));
        if (!fullPath.StartsWith(Path.GetFullPath(newsDir), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(fullPath) ? fullPath : null;
    }

    public static IEnumerable<string> EnumerateAssetFiles(string stackRoot, string patchKey)
    {
        var newsDir = PatchNewsDir(stackRoot, patchKey);
        if (!Directory.Exists(newsDir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(newsDir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.Equals(ArticleFileName, StringComparison.OrdinalIgnoreCase)
                || name.Equals(HtmlFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (CoverFileNames.Any(cover => name.Equals(cover, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return Path.GetRelativePath(newsDir, file).Replace('\\', '/');
        }
    }

    public static string RewriteHtmlForPreview(string html, string stackId, string patchKey)
    {
        var encodedKey = Uri.EscapeDataString(patchKey);
        var baseRoute = $"/api/stacks/{stackId}/migrations/{encodedKey}/news-asset/";
        return RelativeNewsAssetRegex().Replace(html, match =>
        {
            var path = match.Groups[1].Value;
            return $"src=\"{baseRoute}{path}\"";
        });
    }

    public static string ToPublishedAssetId(string articleId, string relativeAssetPath)
    {
        var token = relativeAssetPath.Replace('/', '-').Replace('\\', '-');
        return $"{articleId}--{token}";
    }

    public static string RewriteHtmlForPublish(string html, string stackId, string articleId)
    {
        var imageRoute = $"/api/stacks/{stackId}/launcher/news-image/";
        return RelativeNewsAssetRegex().Replace(html, match =>
        {
            var assetId = ToPublishedAssetId(articleId, match.Groups[1].Value);
            return $"src=\"{imageRoute}{assetId}\"";
        });
    }

    private static string LoadHtmlBody(string stackRoot, string patchKey, PatchNewsArticleFileDto stored)
    {
        var htmlFile = string.IsNullOrWhiteSpace(stored.HtmlFile) ? HtmlFileName : stored.HtmlFile.Trim();
        var htmlPath = Path.Combine(PatchNewsDir(stackRoot, patchKey), htmlFile);
        if (File.Exists(htmlPath))
        {
            return File.ReadAllText(htmlPath);
        }

        return stored.Html ?? string.Empty;
    }

    [GeneratedRegex(@"src=""(?:\./)?(images/[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeNewsAssetRegex();

    private sealed class PatchNewsArticleFileDto
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Date { get; set; }
        public string? Tag { get; set; }
        public int SortOrder { get; set; }
        public bool IsDraft { get; set; }
        public string? Html { get; set; }
        public string? HtmlFile { get; set; }
    }
}

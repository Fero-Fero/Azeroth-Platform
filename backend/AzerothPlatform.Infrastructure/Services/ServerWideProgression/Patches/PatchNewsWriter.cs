using System.Text.Json;
using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

/// <summary>
/// Writes patch <c>news/</c> content to a stack patch folder.
/// </summary>
internal static class PatchNewsWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly HashSet<string> AllowedTags =
        new(StringComparer.OrdinalIgnoreCase) { "patch", "announcement", "expansion", "event", "update", "hotfix" };

    public static void SaveArticle(string stackRoot, string patchKey, SavePatchNewsRequest request, string? dateOverride = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = request.Id?.Trim() ?? string.Empty;
        var title = request.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("News article id is required.");
        }

        if (string.IsNullOrEmpty(title))
        {
            throw new ArgumentException("News article title is required.");
        }

        if (!IsSafeArticleId(id))
        {
            throw new ArgumentException("News article id contains invalid characters.");
        }

        var tag = (request.Tag ?? "patch").Trim().ToLowerInvariant();
        if (!AllowedTags.Contains(tag))
        {
            tag = "patch";
        }

        var newsDir = PatchNewsReader.PatchNewsDir(stackRoot, patchKey);
        Directory.CreateDirectory(newsDir);
        Directory.CreateDirectory(Path.Combine(newsDir, PatchNewsReader.ImagesSubdir));

        var date = (dateOverride ?? request.Date ?? string.Empty).Trim();

        var metadata = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["title"] = title,
            ["date"] = date,
            ["tag"] = tag,
            ["sortOrder"] = request.SortOrder,
            ["isDraft"] = false,
            ["htmlFile"] = PatchNewsReader.HtmlFileName,
        };

        File.WriteAllText(
            Path.Combine(newsDir, PatchNewsReader.ArticleFileName),
            JsonSerializer.Serialize(metadata, JsonOptions) + Environment.NewLine);

        File.WriteAllText(
            Path.Combine(newsDir, PatchNewsReader.HtmlFileName),
            request.Html ?? string.Empty);
    }

    /// <summary>Updates only the <c>date</c> field in an existing article.json.</summary>
    public static bool TryStampDate(string stackRoot, string patchKey, string date, out string? error)
    {
        error = null;
        var articlePath = PatchNewsReader.ArticlePath(stackRoot, patchKey);
        if (!File.Exists(articlePath))
        {
            error = "Patch news article not found.";
            return false;
        }

        Dictionary<string, JsonElement>? stored;
        try
        {
            stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(articlePath),
                JsonOptions);
        }
        catch (Exception ex)
        {
            error = $"Failed to parse news/{PatchNewsReader.ArticleFileName}: {ex.Message}";
            return false;
        }

        if (stored is null)
        {
            error = $"news/{PatchNewsReader.ArticleFileName} is empty.";
            return false;
        }

        stored["date"] = JsonSerializer.SerializeToElement(date.Trim());
        File.WriteAllText(articlePath, JsonSerializer.Serialize(stored, JsonOptions) + Environment.NewLine);
        return true;
    }

    public static string TodayIsoDate() => DateTime.UtcNow.ToString("yyyy-MM-dd");

    public static void SaveCover(string stackRoot, string patchKey, Stream content, string originalFileName)
    {
        var newsDir = PatchNewsReader.PatchNewsDir(stackRoot, patchKey);
        Directory.CreateDirectory(newsDir);

        foreach (var existing in Directory.EnumerateFiles(newsDir))
        {
            var name = Path.GetFileName(existing);
            if (name.StartsWith("cover.", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(existing);
            }
        }

        var extension = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".png";
        }

        var destPath = Path.Combine(newsDir, "cover" + extension.ToLowerInvariant());
        using var output = File.Create(destPath);
        content.CopyTo(output);
    }

    private static bool IsSafeArticleId(string id) =>
        !string.IsNullOrEmpty(id) && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
}

using System.Text.Json;

namespace AzerothPlatform.Infrastructure.Services.ServerWideProgression;

/// <summary>
/// Reads patch <c>config/*.json</c> override files.
/// Empty and comment-only placeholders are skipped; real JSON overrides must parse and validate.
/// </summary>
internal static class PatchConfigJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns <see cref="ConfigOverrideLoadOutcome.Loaded"/> when the file contains at least one override.
    /// Returns <see cref="ConfigOverrideLoadOutcome.Skipped"/> for empty or comment-only placeholders.
    /// Returns <see cref="ConfigOverrideLoadOutcome.Failed"/> when the file has non-placeholder content that cannot be parsed.
    /// </summary>
    public static ConfigOverrideLoadOutcome TryLoadOverrides(
        string jsonContent,
        out Dictionary<string, string>? overrides,
        out string? parseError)
    {
        overrides = null;
        parseError = null;

        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            return ConfigOverrideLoadOutcome.Skipped;
        }

        var trimmed = jsonContent.Trim();
        if (trimmed is "{}" or "[]")
        {
            return ConfigOverrideLoadOutcome.Skipped;
        }

        if (IsCommentOnlyPlaceholder(trimmed))
        {
            return ConfigOverrideLoadOutcome.Skipped;
        }

        try
        {
            overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonContent, JsonOptions);
        }
        catch (Exception ex)
        {
            parseError = ex.Message;
            return ConfigOverrideLoadOutcome.Failed;
        }

        if (overrides is not { Count: > 0 })
        {
            return ConfigOverrideLoadOutcome.Skipped;
        }

        return ConfigOverrideLoadOutcome.Loaded;
    }

    private static bool IsCommentOnlyPlaceholder(string content)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith('#')
                || trimmed.StartsWith("/*", StringComparison.Ordinal)
                || trimmed.StartsWith('*'))
            {
                continue;
            }

            return false;
        }

        return true;
    }
}

internal enum ConfigOverrideLoadOutcome
{
    Skipped,
    Loaded,
    Failed,
}

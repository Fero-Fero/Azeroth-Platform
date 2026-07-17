using System.Text.Json;

namespace AzerothPlatform.Infrastructure.Services.IndividualProgression;

/// <summary>
/// Reads patch <c>config/launcher.json</c> theme overrides (classic/tbc/wotlk).
/// </summary>
internal static class PatchLauncherConfig
{
    public const string ConfigFileName = "launcher.json";

    private static readonly HashSet<string> AllowedThemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "classic", "tbc", "wotlk",
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool TryParseTheme(string jsonContent, out string? theme, out string? error)
    {
        theme = null;
        error = null;

        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            error = "launcher.json is empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonContent);
            if (!document.RootElement.TryGetProperty("theme", out var themeElement)
                || themeElement.ValueKind != JsonValueKind.String)
            {
                error = "launcher.json must contain a string \"theme\" property.";
                return false;
            }

            theme = themeElement.GetString()?.Trim();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (!ValidateTheme(theme, out error))
        {
            return false;
        }

        theme = theme!.ToLowerInvariant();
        return true;
    }

    public static bool ValidateTheme(string? theme, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(theme))
        {
            error = "launcher.json theme must be one of: classic, tbc, wotlk.";
            return false;
        }

        if (!AllowedThemes.Contains(theme))
        {
            error = $"launcher.json theme '{theme}' is invalid. Allowed values: classic, tbc, wotlk.";
            return false;
        }

        return true;
    }
}

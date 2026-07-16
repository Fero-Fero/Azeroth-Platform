using System.Text;
using System.Text.Json;
using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services.Migrations;

/// <summary>
/// Parses patch <c>mpq/mpq.json</c> manifests. Comment-only template files (as used in
/// Azeroth-Platform-Progression) are treated as an empty manifest.
/// </summary>
internal static class MpqManifestReader
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Parses manifest JSON. Returns an empty manifest when the file is blank or comment-only.
    /// Returns <c>null</c> only when the file contains non-comment JSON that fails to parse.
    /// </summary>
    public static MpqManifestDto? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new MpqManifestDto();
        }

        var stripped = StripJsonComments(json).Trim();
        if (string.IsNullOrWhiteSpace(stripped))
        {
            return new MpqManifestDto();
        }

        try
        {
            return JsonSerializer.Deserialize<MpqManifestDto>(stripped, ParseOptions) ?? new MpqManifestDto();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Removes <c>//</c> line and <c>/* */</c> block comments outside JSON string literals.</summary>
    internal static string StripJsonComments(string json)
    {
        var sb = new StringBuilder(json.Length);
        var inString = false;
        var escape = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];

            if (inString)
            {
                sb.Append(c);
                if (escape)
                {
                    escape = false;
                }
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                continue;
            }

            if (c == '/' && i + 1 < json.Length)
            {
                if (json[i + 1] == '/')
                {
                    i += 2;
                    while (i < json.Length && json[i] is not '\n' and not '\r')
                    {
                        i++;
                    }

                    continue;
                }

                if (json[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < json.Length && !(json[i] == '*' && json[i + 1] == '/'))
                    {
                        i++;
                    }

                    i += 2;
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}

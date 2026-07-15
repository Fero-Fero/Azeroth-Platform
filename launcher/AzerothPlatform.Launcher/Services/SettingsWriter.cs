using System.Text.RegularExpressions;
using AzerothPlatform.Launcher.Models;

namespace AzerothPlatform.Launcher.Services;

/// <summary>
/// Writes the pre-defined settings files (Config.wtf, ...) into the client install folder. Files
/// flagged <see cref="LauncherSettingsFile.Overwrite"/> are always rewritten; others are only created
/// when missing so player tweaks are preserved. Config.wtf is handled specially via
/// <see cref="MergeConfigWtfAsync"/> so server-controlled keys are pushed without clobbering the
/// player's own graphics/sound settings.
/// </summary>
public static class SettingsWriter
{
    // Matches a single `SET key value` line in a WTF config, capturing the key and (quoted) value.
    private static readonly Regex SetLineRegex =
        new("^\\s*set\\s+(?<key>\\S+)\\s+(?<value>.*?)\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string RealmlistKey = "realmList";

    /// <summary>
    /// Merges the server-provided Config.wtf over the player's existing one and writes the result.
    /// Every <c>SET key value</c> the server defines wins (so server-side edits propagate on the next
    /// launch), while keys only the player has — resolution, sound, gamma, etc. — are preserved. When
    /// <paramref name="realmListOverride"/> is set it takes final priority for the realmlist line, so
    /// the launcher's editable realmlist (host:port) always wins. Creates the file if it's missing.
    /// </summary>
    public static async Task MergeConfigWtfAsync(
        string installDirectory,
        string configRelativePath,
        string serverContent,
        string? realmListOverride,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configRelativePath))
        {
            return;
        }

        var normalized = configRelativePath.Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.Combine(installDirectory, normalized);

        var clientContent = File.Exists(targetPath)
            ? await File.ReadAllTextAsync(targetPath, cancellationToken)
            : null;

        var merged = MergeConfig(serverContent ?? string.Empty, clientContent);
        if (!string.IsNullOrWhiteSpace(realmListOverride))
        {
            merged = SetConfigValue(merged, RealmlistKey, $"\"{realmListOverride}\"");
        }

        if (clientContent is not null && string.Equals(merged, clientContent, StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, merged, cancellationToken);
    }

    /// <summary>
    /// Fallback used when the server ships no Config.wtf template: ensures WTF/Config.wtf carries the
    /// correct <c>SET realmList "host:port"</c> line (patching just that line so other settings
    /// survive), creating a minimal file if none exists.
    /// </summary>
    public static async Task ApplyRealmlistAsync(
        string installDirectory,
        string configRelativePath,
        string realmList,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(realmList) || string.IsNullOrWhiteSpace(configRelativePath))
        {
            return;
        }

        var normalized = configRelativePath.Replace('/', Path.DirectorySeparatorChar);
        var targetPath = Path.Combine(installDirectory, normalized);

        var clientContent = File.Exists(targetPath)
            ? await File.ReadAllTextAsync(targetPath, cancellationToken)
            : string.Empty;

        var updated = SetConfigValue(clientContent, RealmlistKey, $"\"{realmList}\"");
        if (string.Equals(updated, clientContent, StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, updated, cancellationToken);
    }

    public static async Task ApplyAsync(
        IEnumerable<LauncherSettingsFile> settings,
        string installDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var setting in settings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(setting.TargetRelativePath))
            {
                continue;
            }

            var normalized = setting.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar);
            var targetPath = Path.Combine(installDirectory, normalized);

            if (!setting.Overwrite && File.Exists(targetPath))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllTextAsync(targetPath, setting.Content, cancellationToken);
        }
    }

    /// <summary>
    /// Merges a server Config.wtf over the player's existing one. Every <c>SET key</c> the server
    /// defines overrides the player's; keys the player alone has are kept in place; server-only keys
    /// are appended in server order. Non-<c>SET</c> lines are carried through from the player's file.
    /// </summary>
    private static string MergeConfig(string serverContent, string? clientContent)
    {
        var serverKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var serverOrder = new List<string>();
        foreach (var raw in SplitLines(serverContent))
        {
            var match = SetLineRegex.Match(raw);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups["key"].Value;
            if (!serverKeys.ContainsKey(key))
            {
                serverOrder.Add(key);
            }

            serverKeys[key] = raw.Trim();
        }

        if (string.IsNullOrEmpty(clientContent))
        {
            return NormalizeTrailing(serverContent);
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<string>();
        foreach (var raw in SplitLines(clientContent))
        {
            var match = SetLineRegex.Match(raw);
            if (match.Success && serverKeys.TryGetValue(match.Groups["key"].Value, out var serverLine))
            {
                output.Add(serverLine);
                used.Add(match.Groups["key"].Value);
            }
            else
            {
                output.Add(raw);
            }
        }

        foreach (var key in serverOrder)
        {
            if (!used.Contains(key))
            {
                output.Add(serverKeys[key]);
            }
        }

        return JoinLines(output);
    }

    /// <summary>Replaces (or appends) a single <c>SET key value</c> line, preserving everything else.</summary>
    private static string SetConfigValue(string content, string key, string value)
    {
        var line = $"SET {key} {value}";
        var lines = SplitLines(content).ToList();

        // Drop a single trailing empty line produced by a trailing newline so we don't grow the file.
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var match = SetLineRegex.Match(lines[i]);
            if (match.Success && string.Equals(match.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = line;
                return JoinLines(lines);
            }
        }

        lines.Add(line);
        return JoinLines(lines);
    }

    private static string[] SplitLines(string content) =>
        content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string JoinLines(IEnumerable<string> lines) =>
        string.Join("\n", lines).TrimEnd('\n') + "\n";

    private static string NormalizeTrailing(string content) =>
        content.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n') + "\n";
}

using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Modules;
using AzerothPlatform.Infrastructure.Services.ServerWideProgression;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Rewrites module conf localhost defaults to sidecar DNS names. Custom operator URLs are left alone.
/// </summary>
internal static class ModuleSidecarConf
{
    /// <summary>
    /// Copies missing <c>conf/*.conf.dist</c> from every module checkout into etc (as both
    /// <c>.conf.dist</c> and <c>.conf</c>). Does not overwrite existing files.
    /// </summary>
    public static int SeedFromCheckouts(string etcDir, string? modulesDir)
    {
        if (string.IsNullOrWhiteSpace(modulesDir) || !Directory.Exists(modulesDir))
        {
            return 0;
        }

        var etcModules = Path.Combine(etcDir, "modules");
        Directory.CreateDirectory(etcModules);
        var copied = 0;

        foreach (var moduleDir in Directory.EnumerateDirectories(modulesDir))
        {
            var confDir = Path.Combine(moduleDir, "conf");
            if (!Directory.Exists(confDir))
            {
                continue;
            }

            foreach (var dist in Directory.EnumerateFiles(confDir, "*.conf.dist", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(dist);
                if (string.IsNullOrEmpty(fileName))
                {
                    continue;
                }

                var distTarget = Path.Combine(etcModules, fileName);
                var confTarget = Path.Combine(etcModules, fileName[..^".dist".Length]);
                try
                {
                    if (!File.Exists(distTarget))
                    {
                        File.Copy(dist, distTarget);
                        copied++;
                    }

                    if (!File.Exists(confTarget))
                    {
                        File.Copy(dist, confTarget);
                        copied++;
                    }
                }
                catch (IOException)
                {
                    // Best-effort: worldserver can still seed from the image on first start.
                }
            }
        }

        return copied;
    }

    /// <summary>
    /// Copies missing <c>conf/*.conf.dist</c> from module checkouts into etc, then rewrites sidecar keys.
    /// </summary>
    public static int SeedAndApply(
        string etcDir,
        string? modulesDir,
        IReadOnlyList<ModuleRuntimeSidecar> sidecars)
    {
        SeedFromCheckouts(etcDir, modulesDir);
        if (sidecars.Count == 0)
        {
            return 0;
        }

        return Apply(etcDir, sidecars);
    }

    public static int Apply(string etcDir, IReadOnlyList<ModuleRuntimeSidecar> sidecars)
    {
        if (string.IsNullOrWhiteSpace(etcDir) || !Directory.Exists(etcDir) || sidecars.Count == 0)
        {
            return 0;
        }

        var rewritten = 0;
        foreach (var sidecar in sidecars)
        {
            foreach (var rule in sidecar.ConfRewrites)
            {
                rewritten += ApplyRule(etcDir, rule);
            }
        }

        rewritten += EnsureOllamaOccupationTopicKeys(etcDir);
        return rewritten;
    }

    /// <summary>
    /// Appends empty <c>OllamaChat.OccupationTopics.*</c> keys the module reads but the dist omits,
    /// so worldserver stops logging "Missing property". Existing values are left alone.
    /// </summary>
    internal static int EnsureOllamaOccupationTopicKeys(string etcDir)
    {
        var written = 0;
        foreach (var path in EnumerateConfFiles(etcDir, "mod_ollama_chat.conf"))
        {
            string content;
            try
            {
                content = File.ReadAllText(path);
            }
            catch (IOException)
            {
                continue;
            }

            var updated = content;
            foreach (var key in OllamaSidecar.OccupationTopicKeys)
            {
                if (!ServerConfigValueEditor.TryGetValue(updated, key, out _))
                {
                    updated = ServerConfigValueEditor.SetValue(updated, key, string.Empty);
                }
            }

            if (string.Equals(content, updated, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                File.WriteAllText(path, updated);
                written++;
            }
            catch (IOException)
            {
                // Best-effort.
            }
        }

        return written;
    }

    private static int ApplyRule(string etcDir, ModuleSidecarConfRewrite rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Key) || string.IsNullOrWhiteSpace(rule.SidecarValue))
        {
            return 0;
        }

        var rewritten = 0;
        foreach (var path in EnumerateConfFiles(etcDir, rule.FileNameHint))
        {
            string content;
            try
            {
                content = File.ReadAllText(path);
            }
            catch (IOException)
            {
                continue;
            }

            if (!ServerConfigValueEditor.TryGetValue(content, rule.Key, out var current))
            {
                continue;
            }

            string replacement;
            if (TryRewriteLoopbackHost(current, rule.SidecarValue, out var rewrittenHost))
            {
                replacement = rewrittenHost;
            }
            else if (IsLocalhostDefault(current, rule.LocalhostValues))
            {
                replacement = rule.SidecarValue;
            }
            else
            {
                continue;
            }

            var updated = ServerConfigValueEditor.SetValue(content, rule.Key, replacement);
            if (string.Equals(content, updated, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                File.WriteAllText(path, updated);
                rewritten++;
            }
            catch (IOException)
            {
                // Best-effort.
            }
        }

        return rewritten;
    }

    /// <summary>
    /// Replaces loopback Ollama hosts with the compose sidecar DNS name, keeping path and query
    /// (<c>http://localhost:11434/api/generate</c> → <c>http://ollama:11434/api/generate</c>).
    /// </summary>
    private static bool TryRewriteLoopbackHost(string current, string sidecarValue, out string rewritten)
    {
        rewritten = current;
        var value = current.Trim().Trim('"').Trim('\'');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var currentUri)
            || !Uri.TryCreate(sidecarValue, UriKind.Absolute, out var sidecarUri))
        {
            return false;
        }

        if (!IsLoopbackHost(currentUri.Host))
        {
            return false;
        }

        var builder = new UriBuilder(currentUri)
        {
            Host = sidecarUri.Host,
            Port = sidecarUri.IsDefaultPort ? -1 : sidecarUri.Port,
        };
        rewritten = builder.Uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.Unescaped);
        return !string.Equals(rewritten, value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.Ordinal);

    private static bool IsLocalhostDefault(string current, IReadOnlyList<string> localhostValues)
    {
        var value = current.Trim().Trim('"').Trim('\'');
        foreach (var candidate in localhostValues)
        {
            if (value.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateConfFiles(string etcDir, string? fileNameHint)
    {
        if (!Directory.Exists(etcDir))
        {
            yield break;
        }

        var hint = string.IsNullOrWhiteSpace(fileNameHint) ? null : fileNameHint.Trim();
        foreach (var path in Directory.EnumerateFiles(etcDir, "*.conf*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            if (hint is not null
                && !name.Equals(hint, StringComparison.OrdinalIgnoreCase)
                && !name.Equals(hint + ".dist", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return path;
        }
    }
}

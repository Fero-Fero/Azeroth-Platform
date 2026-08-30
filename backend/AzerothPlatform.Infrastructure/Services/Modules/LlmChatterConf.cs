using AzerothPlatform.Core.Modules;
using AzerothPlatform.Infrastructure.Services.ServerWideProgression;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Points a freshly seeded <c>mod_llm_chatter.conf</c> at the stack's own Ollama sidecar and
/// database. The upstream dist assumes a paid Anthropic key and a database on localhost, so an
/// untouched conf never works here.
/// </summary>
internal static class LlmChatterConf
{
    /// <summary>
    /// Writes a default for every key that is absent or still holds a
    /// <see cref="LlmChatterBridge.StockValues"/> entry. Returns the number of files changed.
    /// </summary>
    public static int Apply(
        string etcDir,
        string model,
        string dbHost,
        int dbPort,
        string dbUser,
        string dbPassword,
        string dbName)
    {
        if (string.IsNullOrWhiteSpace(etcDir) || !Directory.Exists(etcDir))
        {
            return 0;
        }

        var defaults = LlmChatterBridge.ConfDefaults(model, dbHost, dbPort, dbUser, dbPassword, dbName);
        var written = 0;

        foreach (var path in EnumerateConfFiles(etcDir))
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
            foreach (var (key, value) in defaults)
            {
                if (ShouldSeed(updated, key))
                {
                    updated = ServerConfigValueEditor.SetValue(updated, key, value);
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
                // Best-effort: the bridge reports the bad value in its startup health check.
            }
        }

        return written;
    }

    private static bool ShouldSeed(string content, string key)
    {
        if (!ServerConfigValueEditor.TryGetValue(content, key, out var current))
        {
            return true;
        }

        var value = current.Trim().Trim('"').Trim('\'');
        if (value.Length == 0)
        {
            return true;
        }

        return LlmChatterBridge.StockValues.TryGetValue(key, out var stock)
            && stock.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The live conf only. The sibling <c>.conf.dist</c> is left untouched so the stack's root
    /// password is written to exactly one file.
    /// </summary>
    private static IEnumerable<string> EnumerateConfFiles(string etcDir) =>
        Directory
            .EnumerateFiles(etcDir, LlmChatterBridge.ConfFileName, SearchOption.AllDirectories);
}

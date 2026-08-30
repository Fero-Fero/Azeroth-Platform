using AzerothPlatform.Core.Modules;
using AzerothPlatform.Infrastructure.Services.ServerWideProgression;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Whether the AI compose sidecars should run. Selecting a module is not enough:
/// <c>OllamaChat.Enable</c> / <c>OllamaBotControl.Enable</c> / <c>LLMChatter.Enable</c> at 0
/// means worldserver will not call the model, so the Ollama container would only occupy VRAM.
/// An absent key matches the dist default (on).
/// </summary>
internal static class AiChatSidecarNeed
{
    public static bool Ollama(string? etcDir, IEnumerable<string> moduleIds)
    {
        foreach (var id in moduleIds)
        {
            if (string.Equals(id, OllamaSidecar.ChatModuleId, StringComparison.OrdinalIgnoreCase))
            {
                if (IsEnabled(etcDir, OllamaSidecar.ChatConfFileName, OllamaSidecar.ChatEnableKey))
                {
                    return true;
                }
            }
            else if (string.Equals(id, OllamaSidecar.BuddyModuleId, StringComparison.OrdinalIgnoreCase))
            {
                if (IsEnabled(etcDir, OllamaSidecar.BuddyConfFileName, OllamaSidecar.BuddyEnableKey))
                {
                    return true;
                }
            }
            else if (string.Equals(id, LlmChatterBridge.ModuleId, StringComparison.OrdinalIgnoreCase))
            {
                if (IsEnabled(etcDir, LlmChatterBridge.ConfFileName, LlmChatterBridge.EnableKey))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool LlmChatterBridgeContainer(string? etcDir, IEnumerable<string> moduleIds)
    {
        var selected = false;
        foreach (var id in moduleIds)
        {
            if (string.Equals(id, LlmChatterBridge.ModuleId, StringComparison.OrdinalIgnoreCase))
            {
                selected = true;
                break;
            }
        }

        return selected && IsEnabled(etcDir, LlmChatterBridge.ConfFileName, LlmChatterBridge.EnableKey);
    }

    private static bool IsEnabled(string? etcDir, string fileName, string key)
    {
        if (string.IsNullOrWhiteSpace(etcDir) || !Directory.Exists(etcDir))
        {
            return true;
        }

        var preferred = Path.Combine(etcDir, "modules", fileName);
        if (File.Exists(preferred))
        {
            return IsOn(ReadKey(preferred, key));
        }

        foreach (var path in Directory.EnumerateFiles(etcDir, fileName, SearchOption.AllDirectories))
        {
            return IsOn(ReadKey(path, key));
        }

        return true;
    }

    private static string? ReadKey(string path, string key)
    {
        try
        {
            var content = File.ReadAllText(path);
            return ServerConfigValueEditor.TryGetValue(content, key, out var value) ? value : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsOn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim().Trim('"').Trim('\'');
        return !trimmed.Equals("0", StringComparison.OrdinalIgnoreCase)
               && !trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)
               && !trimmed.Equals("off", StringComparison.OrdinalIgnoreCase)
               && !trimmed.Equals("no", StringComparison.OrdinalIgnoreCase);
    }
}

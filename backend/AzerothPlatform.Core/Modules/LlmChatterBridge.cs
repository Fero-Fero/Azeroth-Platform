using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Modules;

/// <summary>
/// Hokken's LLM Chatter. The worldserver half only queues event rows in <c>acore_characters</c>;
/// a separate Python process ("the bridge") drains that queue, calls the LLM, and writes the replies
/// back. Nothing is spoken in game unless the bridge is running, so the stack owns its lifecycle the
/// same way it owns the Ollama sidecar.
/// </summary>
public static class LlmChatterBridge
{
    public const string ModuleId = "mod-llm-chatter";

    /// <summary>Directory under <c>modules/</c>; also the AzerothCore CMake script-loader name.</summary>
    public const string CheckoutFolder = "mod-llm-chatter";

    public const string ServiceName = "llm-chatter-bridge";
    public const string ConfFileName = "mod_llm_chatter.conf";
    public const string EnableKey = "LLMChatter.Enable";

    /// <summary>Path the bridge image mounts the stack's <c>etc</c> volume at.</summary>
    public const string ConfMountPath = "/config";

    public const string ProviderKey = "LLMChatter.Provider";
    public const string ModelKey = "LLMChatter.Model";
    public const string OllamaBaseUrlKey = "LLMChatter.Ollama.BaseUrl";
    public const string DisableThinkingKey = "LLMChatter.Ollama.DisableThinking";
    public const string DatabaseHostKey = "LLMChatter.Database.Host";
    public const string DatabasePortKey = "LLMChatter.Database.Port";
    public const string DatabaseUserKey = "LLMChatter.Database.User";
    public const string DatabasePasswordKey = "LLMChatter.Database.Password";
    public const string DatabaseNameKey = "LLMChatter.Database.Name";

    public const string OllamaProvider = "ollama";

    /// <summary>Per-stack tag, matching the AzerothCore service images.</summary>
    public static string ImageTag(string stackId) => $"localhost/acore/ac-llm-chatter-bridge:{stackId}";

    /// <summary>
    /// Marks the stack as needing the bridge container. The image is per-stack, so it is resolved
    /// when the compose override is generated rather than pinned here; conf rewrites for this module
    /// ride along on <see cref="OllamaSidecar.Default"/>, which it is always selected with.
    /// </summary>
    public static ModuleRuntimeSidecar Sidecar { get; } = new()
    {
        ServiceName = ServiceName,
    };

    /// <summary>
    /// Built with the module checkout as its context. Upstream's compose recipe pip-installs into a
    /// bare <c>python:3.11-slim</c> over a bind mount of the checkout; baking an image instead keeps
    /// the bridge working on external stacks, where the manager ships images and the module sources
    /// never reach the remote engine.
    /// </summary>
    public const string DockerfileContent = """
FROM python:3.11-slim

LABEL azeroth-platform.llm-chatter-bridge=1

WORKDIR /app
COPY tools/ /app/

RUN python -m pip install --no-cache-dir --upgrade pip \
    && if [ -f /app/requirements.txt ]; then python -m pip install --no-cache-dir -r /app/requirements.txt; fi

ENV PYTHONUNBUFFERED=1

CMD ["python", "llm_chatter_bridge.py", "--config", "/config/modules/mod_llm_chatter.conf"]
""";

    /// <summary>
    /// Values the dist ships that no stack can use as-is. A key still holding one of these is
    /// treated as unset, so seeding it does not overwrite an operator's own choice.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> StockValues =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [ProviderKey] = ["anthropic"],
            [ModelKey] = ["claude-haiku-4-5-20251001"],
            [DatabaseHostKey] = ["localhost", "127.0.0.1"],
            [DatabasePasswordKey] = ["acore", "password"],
        };

    /// <summary>
    /// Values written into <c>mod_llm_chatter.conf</c> after it is seeded from the module checkout.
    /// The dist targets Anthropic with a paid API key; these repoint it at the stack's own Ollama
    /// sidecar and at the stack database, neither of which the operator can know in advance.
    /// <paramref name="dbPassword"/> is the stack's MySQL root password.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ConfDefaults(
        string? model,
        string dbHost,
        int dbPort,
        string dbUser,
        string dbPassword,
        string dbName) =>
    [
        new(ProviderKey, OllamaProvider),
        new(ModelKey, string.IsNullOrWhiteSpace(model) ? OllamaSidecar.Model : model.Trim()),
        // Reasoning models emit <think> blocks that break the module's structured-JSON parsing.
        new(DisableThinkingKey, "1"),
        new(DatabaseHostKey, dbHost),
        new(DatabasePortKey, dbPort.ToString()),
        new(DatabaseUserKey, dbUser),
        new(DatabasePasswordKey, dbPassword),
        new(DatabaseNameKey, dbName),
    ];
}

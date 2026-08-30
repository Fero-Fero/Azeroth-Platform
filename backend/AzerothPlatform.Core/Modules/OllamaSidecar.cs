using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Modules;

/// <summary>
/// Accelerator the Ollama sidecar should use. The manager probes the stack's Docker engine
/// (local daemon or <c>--context</c> SSH engine) and picks the first match: NVIDIA, then ROCm,
/// then Vulkan, then CPU.
///
/// <para>
/// Docker Desktop on Windows exposes NVIDIA through the WSL2 GPU toolkit. AMD/Intel device nodes
/// (<c>/dev/kfd</c>, <c>/dev/dri</c>) are only visible when WSL passthrough provides them; otherwise
/// the sidecar stays on CPU. CPU is a first-class backend, not a failure. Apple Metal is out of
/// scope for Linux containers.
/// </para>
/// </summary>
public enum GpuBackend
{
    Cpu = 0,
    Nvidia = 1,
    Rocm = 2,
    Vulkan = 3,
}

/// <summary>
/// Shared Ollama compose sidecar declared by both Bot Buddy and Ollama Chat.
/// </summary>
public static class OllamaSidecar
{
    public const string ServiceName = "ollama";
    public const string ChatModuleId = "mod-ollama-chat";
    public const string BuddyModuleId = "mod-ollama-bot-buddy";
    public const string ChatConfFileName = "mod_ollama_chat.conf";
    public const string BuddyConfFileName = "mod_ollama_bot_buddy.conf";
    public const string ChatEnableKey = "OllamaChat.Enable";
    public const string BuddyEnableKey = "OllamaBotControl.Enable";
    public const string Image = "ollama/ollama:latest";
    public const string RocmImage = "ollama/ollama:rocm";
    public const string Model = "llama3.2:1b";

    /// <summary>
    /// Playerbots built-in talk that overlaps Ollama Chat / Bot Buddy. Express Setup writes these
    /// when such a module is selected; other server types apply them from the stack-status suggestion.
    /// LLM Chatter is excluded: it layers on top of playerbot chatter instead of replacing it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PlayerbotsChatterDisable =
        new Dictionary<string, string>
        {
            ["AiPlayerbot.EnableBroadcasts"] = "0",
            ["AiPlayerbot.RandomBotTalk"] = "0",
            ["AiPlayerbot.RandomBotEmote"] = "0",
            ["AiPlayerbot.RandomBotSuggestDungeons"] = "0",
            ["AiPlayerbot.EnableGreet"] = "0",
            ["AiPlayerbot.GuildFeedback"] = "0",
            ["AiPlayerbot.RandomBotSayWithoutMaster"] = "0",
        };

    /// <summary>
    /// Occupation topic lists the chat module always reads. The upstream dist only ships five
    /// populated pools; the rest are "empty = skip" but AzerothCore still warns when the key is
    /// absent. We write empty keys so the warning (and any null-default lookup) goes away.
    /// </summary>
    public static readonly IReadOnlyList<string> OccupationTopicKeys =
    [
        "OllamaChat.OccupationTopics.go_grind",
        "OllamaChat.OccupationTopics.wander_random",
        "OllamaChat.OccupationTopics.do_quest",
        "OllamaChat.OccupationTopics.rest",
        "OllamaChat.OccupationTopics.wander_npc",
        "OllamaChat.OccupationTopics.travel_flight",
        "OllamaChat.OccupationTopics.travel_mount",
        "OllamaChat.OccupationTopics.outdoor_pvp",
        "OllamaChat.OccupationTopics.loiter",
        "OllamaChat.OccupationTopics.repair_sell",
        "OllamaChat.OccupationTopics.dummy",
    ];

    /// <summary>
    /// Modules whose own chat stands in for the playerbots built-in talk, so
    /// <see cref="PlayerbotsChatterDisable"/> applies when one of them is selected.
    /// </summary>
    public static bool ReplacesPlayerbotsChatter(string? moduleId) =>
        string.Equals(moduleId, ChatModuleId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(moduleId, BuddyModuleId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every module that drives the shared Ollama sidecar. Only one may be selected at a time
    /// (each install hook declares the others in <c>ConflictsWith</c>).
    /// </summary>
    public static bool IsAiChatModuleId(string? moduleId) =>
        ReplacesPlayerbotsChatter(moduleId)
        || string.Equals(moduleId, LlmChatterBridge.ModuleId, StringComparison.OrdinalIgnoreCase);
    public const string ModelsVolumeName = "azeroth-platform-ollama-models";
    public const string ModelsVolumeKey = "ollama_models";
    public const string InternalHost = "ollama";
    public const int InternalPort = 11434;

    /// <summary>
    /// Hub image for <paramref name="backend"/>. Official <c>ollama/ollama:latest</c> is CUDA/CPU/Vulkan;
    /// ROCm needs the <c>:rocm</c> tag. A custom image is left unchanged.
    /// </summary>
    public static string ResolveImage(string? requested, GpuBackend backend)
    {
        var image = string.IsNullOrWhiteSpace(requested) ? Image : requested.Trim();
        if (backend == GpuBackend.Rocm && IsDefaultHubImage(image))
        {
            return RocmImage;
        }

        return image;
    }

    /// <summary>
    /// Path under the models volume where Ollama stores a model's manifest
    /// (e.g. <c>llama3.2:1b</c> → <c>models/manifests/registry.ollama.ai/library/llama3.2/1b</c>).
    /// </summary>
    public static string LibraryManifestRelativePath(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var trimmed = model.Trim();
        var slash = trimmed.IndexOf('/');
        var ns = "library";
        var rest = trimmed;
        if (slash >= 0)
        {
            ns = trimmed[..slash];
            rest = trimmed[(slash + 1)..];
        }

        var colon = rest.LastIndexOf(':');
        var name = colon >= 0 ? rest[..colon] : rest;
        var tag = colon >= 0 ? rest[(colon + 1)..] : "latest";
        return $"models/manifests/registry.ollama.ai/{ns}/{name}/{tag}";
    }

    private static bool IsDefaultHubImage(string image) =>
        image.Equals(Image, StringComparison.OrdinalIgnoreCase)
        || image.Equals("ollama/ollama", StringComparison.OrdinalIgnoreCase);

    public static ModuleRuntimeSidecar Default { get; } = new()
    {
        ServiceName = ServiceName,
        Image = Image,
        Model = Model,
        ModelsVolumeName = ModelsVolumeName,
        ModelsVolumeKey = ModelsVolumeKey,
        ConfRewrites =
        [
            new ModuleSidecarConfRewrite
            {
                Key = "OllamaBotControl.Url",
                FileNameHint = "mod_ollama_bot_buddy.conf",
                LocalhostValues =
                [
                    "http://localhost:11434/api/generate",
                    "http://127.0.0.1:11434/api/generate",
                ],
                SidecarValue = $"http://{InternalHost}:{InternalPort}/api/generate",
            },
            new ModuleSidecarConfRewrite
            {
                Key = "OllamaChat.Url",
                FileNameHint = "mod_ollama_chat.conf",
                LocalhostValues =
                [
                    "http://localhost:11434/api/generate",
                    "http://127.0.0.1:11434/api/generate",
                    "http://localhost:11434",
                    "http://127.0.0.1:11434",
                ],
                SidecarValue = $"http://{InternalHost}:{InternalPort}/api/generate",
            },
            new ModuleSidecarConfRewrite
            {
                Key = "OllamaChat.ApiEndpoint",
                FileNameHint = "mod_ollama_chat.conf",
                LocalhostValues =
                [
                    "http://localhost:11434",
                    "http://127.0.0.1:11434",
                ],
                SidecarValue = $"http://{InternalHost}:{InternalPort}",
            },
            new ModuleSidecarConfRewrite
            {
                Key = LlmChatterBridge.OllamaBaseUrlKey,
                FileNameHint = LlmChatterBridge.ConfFileName,
                // The dist ships host.docker.internal, which resolves to the Docker host rather than
                // the sidecar container; it is a stock default like the loopback ones, not an operator choice.
                LocalhostValues =
                [
                    "http://host.docker.internal:11434",
                    "http://localhost:11434",
                    "http://127.0.0.1:11434",
                ],
                SidecarValue = $"http://{InternalHost}:{InternalPort}",
            },
        ],
    };
}

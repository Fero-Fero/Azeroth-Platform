using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Modules;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;

/// <summary>
/// Hokken LLM Chatter. Mutually exclusive with the other AI chat modules: they all drive bot
/// speech, and only one may own it. Starts the shared Ollama sidecar plus its own Python bridge,
/// without which the module queues events that nobody drains.
/// </summary>
public sealed class LlmChatterInstallHook : IModuleInstallHook
{
    public const string CatalogId = LlmChatterBridge.ModuleId;

    /// <summary>libcurl backs the module's HTTP calls; the AzerothCore image ships neither header set.</summary>
    public static readonly string[] AptPackages =
    [
        "libcurl4-openssl-dev",
        "nlohmann-json3-dev",
    ];

    public string ModuleId => CatalogId;

    public ModuleCompileProfile Compile { get; } = new()
    {
        ExtraAptPackages = AptPackages,
        ConflictsWith = [OllamaChatInstallHook.CatalogId, OllamaBotBuddyInstallHook.CatalogId],
        RuntimeSidecars = [OllamaSidecar.Default, LlmChatterBridge.Sidecar],
    };

    public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

    public Task<ModuleInstallContribution> InstallAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ModuleInstallContribution());
}

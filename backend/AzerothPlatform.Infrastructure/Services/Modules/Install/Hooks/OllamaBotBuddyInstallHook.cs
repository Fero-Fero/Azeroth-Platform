using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Modules;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;

/// <summary>
/// DustinHendrickson Ollama Bot Buddy. Extra curl/json headers for the module-check
/// and AzerothCore Docker build. Mutually exclusive with the other AI chat modules.
/// Starts the shared Ollama sidecar at runtime.
/// </summary>
public sealed class OllamaBotBuddyInstallHook : IModuleInstallHook
{
    public const string CatalogId = "mod-ollama-bot-buddy";

    public static readonly string[] AptPackages =
    [
        "libcurl4-openssl-dev",
        "nlohmann-json3-dev",
    ];

    public string ModuleId => CatalogId;

    public ModuleCompileProfile Compile { get; } = new()
    {
        ExtraAptPackages = AptPackages,
        ConflictsWith = [OllamaChatInstallHook.CatalogId, LlmChatterInstallHook.CatalogId],
        RuntimeSidecars = [OllamaSidecar.Default],
    };

    public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

    public Task<ModuleInstallContribution> InstallAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ModuleInstallContribution());
}

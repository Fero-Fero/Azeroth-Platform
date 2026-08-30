using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Modules;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;

/// <summary>
/// Fero-Fero Ollama Chat (fork of DustinHendrickson). JSON and cpp-httplib are bundled
/// in the module; fmt and OpenSSL come from the AzerothCore image. Mutually exclusive
/// with the other AI chat modules. Starts the shared Ollama sidecar at runtime.
/// </summary>
public sealed class OllamaChatInstallHook : IModuleInstallHook
{
    public const string CatalogId = "mod-ollama-chat";

    public string ModuleId => CatalogId;

    public ModuleCompileProfile Compile { get; } = new()
    {
        ConflictsWith = [OllamaBotBuddyInstallHook.CatalogId, LlmChatterInstallHook.CatalogId],
        RuntimeSidecars = [OllamaSidecar.Default],
    };

    public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

    public Task<ModuleInstallContribution> InstallAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ModuleInstallContribution());
}

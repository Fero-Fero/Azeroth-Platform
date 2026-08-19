using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;

/// <summary>
/// Rochet2 AIO server Lua. The client addon already ships with the platform client
/// (hidden as a default addon) so only <c>AIO_Server</c> is deposited into lua_scripts.
/// </summary>
public sealed class AioInstallHook : IModuleInstallHook
{
    public const string CatalogId = "aio";

    public string ModuleId => CatalogId;

    public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

    public async Task<ModuleInstallContribution> InstallAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default)
    {
        await context.Helpers.IncludeLua("AIO_Server", string.Empty, cancellationToken);
        return context.Helpers.Contribution;
    }
}

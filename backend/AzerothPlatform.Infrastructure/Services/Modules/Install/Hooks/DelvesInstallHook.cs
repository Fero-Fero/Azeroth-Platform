using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;

/// <summary>
/// Delves extra data: named DBC CSVs, unpacked client files packed as overlay <c>patch-E.MPQ</c>,
/// server map/mmap/vmap files, and lua_scripts. World SQL under <c>data/sql/db-world</c> is universal.
/// </summary>
public sealed class DelvesInstallHook : IModuleInstallHook
{
    public const string CatalogId = "delves";
    public const string OverlayMpqFileName = "patch-E.MPQ";

    public string ModuleId => CatalogId;

    public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

    public async Task<ModuleInstallContribution> InstallAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default)
    {
        await context.Helpers.IncludeCsvDirectory(
            Path.Combine("DBC_CSV", "DBFilesClient"),
            cancellationToken);
        await context.Helpers.PackMpqDirectory("MPQ", OverlayMpqFileName, cancellationToken);
        await context.Helpers.IncludeMaps("Server Map Files", cancellationToken);
        await context.Helpers.IncludeLua("lua_scripts", string.Empty, cancellationToken);
        return context.Helpers.Contribution;
    }
}

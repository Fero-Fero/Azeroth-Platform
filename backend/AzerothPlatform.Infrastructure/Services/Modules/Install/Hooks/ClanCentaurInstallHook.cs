using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;

/// <summary>
/// ClanCentaur is SQL + DBC only. World SQL lives under <c>data/sql/world/base</c> (not
/// AzerothCore <c>data/sql/db-world</c>), so it is extra-data. Faction.csv is already a DBC CSV.
/// </summary>
public sealed class ClanCentaurInstallHook : IModuleInstallHook
{
    public const string CatalogId = "clancentaur";

    public string ModuleId => CatalogId;

    public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

    public async Task<ModuleInstallContribution> InstallAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default)
    {
        await context.Helpers.IncludeCsv(
            Path.Combine("DBClientFiles", "Faction.csv"),
            cancellationToken);
        await context.Helpers.IncludeSql(
            Path.Combine("data", "sql", "world", "base", "ClanCentaur_Items.sql"),
            cancellationToken);
        await context.Helpers.IncludeSql(
            Path.Combine("data", "sql", "world", "base", "ClanCentaur_NPCVendors.sql"),
            cancellationToken);
        return context.Helpers.Contribution;
    }
}

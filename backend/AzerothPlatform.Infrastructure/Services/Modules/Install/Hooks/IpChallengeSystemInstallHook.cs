using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;

/// <summary>
/// IP Challenge System keeps SQL under <c>sql/</c> (not <c>data/sql/</c>), so it is extra-data.
/// </summary>
public sealed class IpChallengeSystemInstallHook : IModuleInstallHook
{
    public const string CatalogId = "mod-ip-challengesystem";

    public string ModuleId => CatalogId;

    public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

    public async Task<ModuleInstallContribution> InstallAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default)
    {
        await context.Helpers.IncludeSql(
            Path.Combine("sql", "characters", "001_create_ip_challenge_runs.sql"),
            cancellationToken);
        await context.Helpers.IncludeSql(
            Path.Combine("sql", "characters", "002_create_ip_permadeath.sql"),
            cancellationToken);
        return context.Helpers.Contribution;
    }
}

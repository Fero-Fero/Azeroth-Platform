using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;

/// <summary>
/// BMAH is not a C++ module. Extra-data copies Eluna scripts and the client addon from named paths.
/// </summary>
public sealed class BlackMarketAuctionHouseInstallHook : IModuleInstallHook
{
    public const string CatalogId = "black-market-auction-house";

    public string ModuleId => CatalogId;

    public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

    public async Task<ModuleInstallContribution> InstallAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default)
    {
        await context.Helpers.IncludeAddon(
            Path.Combine("Client Files", "AddOns", "BlackMarketUI"),
            "BlackMarketUI",
            cancellationToken);
        await context.Helpers.IncludeLua(
            Path.Combine("Server Files", "lua_scripts"),
            string.Empty,
            cancellationToken);
        return context.Helpers.Contribution;
    }
}

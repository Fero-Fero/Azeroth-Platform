using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;

/// <summary>
/// Extra-data recipe for PetBattleSystem: stock <c>Interface/AddOns/PetBattleUI</c> only.
/// </summary>
public sealed class PetBattleInstallHook : IModuleInstallHook
{
    public const string CatalogId = "mod-pet-battle";

    public string ModuleId => CatalogId;

    public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

    public async Task<ModuleInstallContribution> InstallAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default)
    {
        await context.Helpers.IncludeAddon(
            Path.Combine("Interface", "AddOns", "PetBattleUI"),
            "PetBattleUI",
            cancellationToken);
        return context.Helpers.Contribution;
    }
}

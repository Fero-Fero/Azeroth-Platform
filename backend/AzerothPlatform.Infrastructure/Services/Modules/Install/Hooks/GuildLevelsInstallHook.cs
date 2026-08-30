using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;

namespace AzerothPlatform.Infrastructure.Services.Modules.Install.Hooks;

/// <summary>
/// Guild Levels extra data: stock client addon plus the ALE <c>.ext</c> script.
/// </summary>
public sealed class GuildLevelsInstallHook : IModuleInstallHook
{
    public const string CatalogId = "mod-guild-levels";

    public string ModuleId => CatalogId;

    public Task<IReadOnlyList<ModuleInstallChoiceGroup>> DescribeChoicesAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModuleInstallChoiceGroup>>([]);

    public async Task<ModuleInstallContribution> InstallAsync(
        ModuleInstallContext context, CancellationToken cancellationToken = default)
    {
        await context.Helpers.IncludeAddon(
            Path.Combine("client_addon", "GuildLevels"),
            "GuildLevels",
            cancellationToken);
        await context.Helpers.IncludeLua(
            Path.Combine("lua", "extensions", "guild_levels", "guild_levels.ext"),
            Path.Combine("extensions", "guild_levels", "guild_levels.ext"),
            cancellationToken);
        return context.Helpers.Contribution;
    }
}

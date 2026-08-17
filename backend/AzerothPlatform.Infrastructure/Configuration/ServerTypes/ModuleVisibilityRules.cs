using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Configuration.ServerTypes;

public static class ModuleVisibilityRules
{
    public static IReadOnlyList<ModuleVisibilityRule> All { get; } =
    [
        new()
        {
            ModuleId = "mod-playerbots",
            HiddenForServerTypes = [ServerType.Playerbots, ServerType.NpcBots]
        },
        new()
        {
            ModuleId = "mod-individual-progression",
            VisibleForServerTypes = [ServerType.IndividualProgression]
        },
        new()
        {
            ModuleId = "mod-dungeon-clear",
            HiddenForServerTypes = [ServerType.NpcBots]
        },
        new()
        {
            ModuleId = "mod-playerbot-dungeon-sim",
            HiddenForServerTypes = [ServerType.NpcBots]
        }
    ];
}

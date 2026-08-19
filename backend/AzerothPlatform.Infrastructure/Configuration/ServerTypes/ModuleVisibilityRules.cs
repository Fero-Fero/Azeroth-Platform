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
            VisibleForServerTypes = [ServerType.IndividualProgression, ServerType.Express]
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
        },
        new()
        {
            ModuleId = "mod-playerbots-artisans",
            HiddenForServerTypes = [ServerType.NpcBots]
        },
        new()
        {
            ModuleId = "mod-optimal-bot-raid",
            HiddenForServerTypes = [ServerType.NpcBots]
        },
        new()
        {
            ModuleId = "mod-world-buff-bots",
            HiddenForServerTypes = [ServerType.NpcBots]
        },
        new()
        {
            ModuleId = "mod-ollama-bot-buddy",
            HiddenForServerTypes = [ServerType.NpcBots]
        },
        new()
        {
            ModuleId = "mod-ollama-bot-buddy-advanced",
            HiddenForServerTypes = [ServerType.NpcBots]
        },
        new()
        {
            ModuleId = "mod-ip-challengesystem",
            VisibleForServerTypes = [ServerType.IndividualProgression, ServerType.Express]
        },
        new()
        {
            ModuleId = "mod-character-services",
            VisibleForServerTypes = [ServerType.IndividualProgression, ServerType.Express]
        }
    ];
}

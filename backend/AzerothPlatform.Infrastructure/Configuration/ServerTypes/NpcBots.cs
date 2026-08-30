using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Configuration.ServerTypes;

public static class NpcBots
{
    public static ServerTypeDefinition Catalog { get; } = new()
    {
        Id = ServerType.NpcBots,
        Enabled = true,
        DisplayName = "NPCBots",
        Description = "AzerothCore with NPCBots integrated - hire NPC companions directly in the world.",
        Icon = "users",
        CoreRepositoryUrl = "https://github.com/trickerer/AzerothCore-wotlk-with-NPCBots.git",
        CoreBranch = "npcbots_3.3.5",
        BundledModuleIds = []
    };
}

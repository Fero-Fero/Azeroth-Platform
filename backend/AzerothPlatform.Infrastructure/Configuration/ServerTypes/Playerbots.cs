using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Configuration.ServerTypes;

public static class Playerbots
{
    public static ServerTypeDefinition Catalog { get; } = new()
    {
        Id = ServerType.Playerbots,
        Enabled = true,
        DisplayName = "Playerbots",
        Description = "Official Playerbots fork with the module already integrated so you can level and raid solo.",
        Icon = "bot",
        CoreRepositoryUrl = "https://github.com/mod-playerbots/azerothcore-wotlk.git",
        CoreBranch = "Playerbot",
        BundledModuleIds = ["mod-playerbots"]
    };
}

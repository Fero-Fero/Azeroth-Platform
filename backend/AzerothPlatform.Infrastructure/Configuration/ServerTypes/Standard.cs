using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Configuration.ServerTypes;

public static class Standard
{
    public static ServerTypeDefinition Catalog { get; } = new()
    {
        Id = ServerType.Standard,
        Enabled = true,
        DisplayName = "Standard",
        Description = "Vanilla AzerothCore - the classic WotLK experience. Playerbots can be added as a module.",
        Icon = "server",
        CoreRepositoryUrl = "https://github.com/azerothcore/azerothcore-wotlk.git",
        CoreBranch = "master",
        BundledModuleIds = []
    };
}

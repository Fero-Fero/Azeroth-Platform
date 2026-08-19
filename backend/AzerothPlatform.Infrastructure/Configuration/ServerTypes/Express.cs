using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Configuration.ServerTypes;

public static class Express
{
    public static ServerTypeDefinition Catalog { get; } = new()
    {
        Id = ServerType.Express,
        Enabled = true,
        DisplayName = "Express Setup",
        Description = "Local one-click setup with Individual Progression, playerbots, and a first patch applied automatically.",
        Icon = "zap",
        CoreRepositoryUrl = "https://github.com/Grimfeather/azerothcore-wotlk.git",
        CoreBranch = "master",
        LocalOnly = true,
        BundledModuleIds = [],
        RequiredModuleIds =
        [
            "mod-individual-progression",
            "mod-playerbots",
            "mod-optimal-bot-raid",
            "mod-ah-bot"
        ]
    };
}

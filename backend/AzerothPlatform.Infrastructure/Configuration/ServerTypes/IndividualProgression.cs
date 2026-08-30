using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Configuration.ServerTypes;

public static class IndividualProgression
{
    public static ServerTypeDefinition Catalog { get; } = new()
    {
        Id = ServerType.IndividualProgression,
        Enabled = true,
        DisplayName = "Individual Progression",
        Description = "Grimfeather fork that simulates progression through expansions and tiers, per character.",
        Icon = "trending-up",
        CoreRepositoryUrl = "https://github.com/Grimfeather/azerothcore-wotlk.git",
        CoreBranch = "master",
        BundledModuleIds = [],
        RequiredModuleIds = ["mod-individual-progression"]
    };
}

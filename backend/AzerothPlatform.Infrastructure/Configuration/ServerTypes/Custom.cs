using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Configuration.ServerTypes;

public static class Custom
{
    public static ServerTypeDefinition Catalog { get; } = new()
    {
        Id = ServerType.Custom,
        Enabled = true,
        DisplayName = "Custom Fork",
        Description = "Build from your own AzerothCore fork — paste a GitHub repository URL and branch.",
        Icon = "git-fork",
        CoreRepositoryUrl = string.Empty,
        CoreBranch = "master",
        AllowCustomRepository = true,
        BundledModuleIds = []
    };
}

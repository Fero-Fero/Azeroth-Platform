using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class ModuleDependencyValidationTests
{
    [Fact]
    public async Task ValidateAsync_rejects_dungeon_sim_without_dungeon_clear()
    {
        await using var db = CreateDbContext();
        var modules = new Mock<IModuleCatalogService>();
        modules
            .Setup(service => service.ListAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ModuleDto
                {
                    Id = "mod-dungeon-clear",
                    Name = "Dungeon Clear",
                    Repository = "https://github.com/jrad7/mod-dungeon-clear.git",
                    Branch = "master",
                    IsBuiltIn = true,
                },
                new ModuleDto
                {
                    Id = "mod-playerbot-dungeon-sim",
                    Name = "Playerbot Dungeon Sim",
                    Repository = "https://github.com/TopHatMan/mod-playerbot-dungeon-sim.git",
                    Branch = "main",
                    IsBuiltIn = true,
                    RequiredModuleIds = ["mod-dungeon-clear"],
                },
            ]);

        var serverTypes = new Mock<IServerTypeCatalog>();
        serverTypes
            .Setup(catalog => catalog.IsModuleVisible(It.IsAny<string>(), It.IsAny<ServerType>()))
            .Returns(true);
        serverTypes
            .Setup(catalog => catalog.GetRequiredModuleIds(It.IsAny<ServerType>()))
            .Returns(Array.Empty<string>());

        var armoryAccounts = new Mock<IArmoryAccountsService>();
        var validator = new StackConfigurationValidator(
            db,
            modules.Object,
            serverTypes.Object,
            armoryAccounts.Object);

        var configuration = new StackConfigurationDto
        {
            StackName = "test-stack",
            ServerType = ServerType.Playerbots,
            ModuleIds = ["mod-playerbot-dungeon-sim"],
            Database = new DatabaseConfigDto { RootPassword = "password123", Port = 3306 },
            Ports = new PortConfigDto { AuthServer = 3724, WorldServer = 8085, SoapPort = 7878 },
            Advanced = new AdvancedConfigDto { RealmName = "Test Realm", MaxPlayers = 100 },
        };

        var result = await validator.ValidateAsync(configuration);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.Field == "moduleIds" &&
            error.Message.Contains("Playerbot Dungeon Sim", StringComparison.Ordinal) &&
            error.Message.Contains("Dungeon Clear", StringComparison.Ordinal));
    }

    private static AzerothCoreDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AzerothCoreDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var db = new AzerothCoreDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }
}

using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class ServerTypeRequiredModuleTests
{
    [Fact]
    public async Task ValidateAsync_rejects_individual_progression_without_required_module()
    {
        await using var db = CreateDbContext();
        var modules = CreateModuleCatalogMock();
        var serverTypes = CreateServerTypeCatalogMock();
        var validator = CreateValidator(db, modules, serverTypes);

        var configuration = CreateBaseConfiguration(ServerType.IndividualProgression, []);

        var result = await validator.ValidateAsync(configuration);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.Field == "moduleIds" &&
            error.Message.Contains("Individual Progression", StringComparison.Ordinal) &&
            error.Message.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateAsync_accepts_individual_progression_with_required_module()
    {
        await using var db = CreateDbContext();
        var modules = CreateModuleCatalogMock();
        var serverTypes = CreateServerTypeCatalogMock();
        var validator = CreateValidator(db, modules, serverTypes);

        var configuration = CreateBaseConfiguration(
            ServerType.IndividualProgression,
            ["mod-individual-progression"]);

        var result = await validator.ValidateAsync(configuration);

        result.Errors.Should().NotContain(error => error.Field == "moduleIds");
    }

    [Fact]
    public void GetServerTypes_exposes_required_modules_for_individual_progression()
    {
        var catalog = new ServerTypeCatalog(
            Microsoft.Extensions.Options.Options.Create(ServerTypeCatalogOptions.Defaults));

        var type = catalog.GetServerTypes().Single(item => item.Id == ServerType.IndividualProgression);
        type.RequiredModuleIds.Should().Contain("mod-individual-progression");
        type.RequiredModuleIds.Should().NotContain("mod-playerbots");
        catalog.GetRequiredModuleIds(ServerType.IndividualProgression)
            .Should().Contain("mod-individual-progression");
        catalog.GetRequiredModuleIds(ServerType.IndividualProgression)
            .Should().NotContain("mod-playerbots");
        catalog.GetBundledModuleIds(ServerType.Playerbots).Should().Contain("mod-playerbots");
        catalog.GetBundledModuleIds(ServerType.Standard).Should().BeEmpty();
    }

    [Fact]
    public void GetServerTypes_exposes_express_as_local_only_with_required_modules()
    {
        var catalog = new ServerTypeCatalog(
            Microsoft.Extensions.Options.Options.Create(ServerTypeCatalogOptions.Defaults));

        var type = catalog.GetServerTypes().Single(item => item.Id == ServerType.Express);
        type.LocalOnly.Should().BeTrue();
        type.RequiredModuleIds.Should().Contain("mod-individual-progression");
        type.RequiredModuleIds.Should().Contain("mod-playerbots");
        type.RequiredModuleIds.Should().Contain("mod-optimal-bot-raid");
        type.RequiredModuleIds.Should().Contain("mod-ah-bot");
        type.CoreRepositoryUrl.Should().Contain("Grimfeather");
    }

    private static StackConfigurationValidator CreateValidator(
        AzerothCoreDbContext db,
        Mock<IModuleCatalogService> modules,
        Mock<IServerTypeCatalog> serverTypes)
    {
        var armoryAccounts = new Mock<IArmoryAccountsService>();
        return new StackConfigurationValidator(db, modules.Object, serverTypes.Object, armoryAccounts.Object);
    }

    private static Mock<IModuleCatalogService> CreateModuleCatalogMock()
    {
        var modules = new Mock<IModuleCatalogService>();
        modules
            .Setup(service => service.ListAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ModuleDto
                {
                    Id = "mod-individual-progression",
                    Name = "Individual Progression",
                    Repository = "https://github.com/Grimfeather/mod-individual-progression",
                    Branch = "master",
                    IsBuiltIn = true,
                },
            ]);
        return modules;
    }

    private static Mock<IServerTypeCatalog> CreateServerTypeCatalogMock()
    {
        var serverTypes = new Mock<IServerTypeCatalog>();
        serverTypes
            .Setup(catalog => catalog.IsModuleVisible("mod-individual-progression", ServerType.IndividualProgression))
            .Returns(true);
        serverTypes
            .Setup(catalog => catalog.GetRequiredModuleIds(ServerType.IndividualProgression))
            .Returns(["mod-individual-progression"]);
        serverTypes
            .Setup(catalog => catalog.GetRequiredModuleIds(It.Is<ServerType>(type => type != ServerType.IndividualProgression)))
            .Returns(Array.Empty<string>());
        serverTypes
            .Setup(catalog => catalog.AllowsCustomRepository(It.IsAny<ServerType>()))
            .Returns(false);
        return serverTypes;
    }

    private static StackConfigurationDto CreateBaseConfiguration(ServerType serverType, string[] moduleIds) =>
        new()
        {
            StackName = "test-stack",
            ServerType = serverType,
            ModuleIds = moduleIds.ToList(),
            Database = new DatabaseConfigDto { RootPassword = "password123", Port = 3306 },
            Ports = new PortConfigDto { AuthServer = 3724, WorldServer = 8085, SoapPort = 7878 },
            Advanced = new AdvancedConfigDto { RealmName = "Test Realm", MaxPlayers = 100 },
        };

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

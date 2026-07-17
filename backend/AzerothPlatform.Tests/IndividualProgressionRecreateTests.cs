using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.IndividualProgression;
using AzerothPlatform.Infrastructure.Services.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class IndividualProgressionRecreateTests
{
    [Fact]
    public async Task RecreateMissingPatchesAsync_creates_only_missing_templates()
    {
        var stackId = "ip-recreate";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-ip-recreate-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        try
        {
            await using var db = CreateDbContext(new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
                ModuleIdsJson = """["mod-individual-progression"]""",
            });
            var service = CreateSyncService(db, buildsPath);

            await service.BootstrapAsync(stackId);
            SeedTestProgressionRepo(stackRoot);
            SeedStackPatchesFromRepo(stackRoot);
            var startKey = PatchFolderNames.Format(new PatchIndex(1, 0, explicitSub1: true), "Start");
            Directory.Delete(MigrationLayout.PatchDir(stackRoot, startKey), recursive: true);

            var result = await service.RecreateMissingPatchesAsync(stackId);

            result.MissingBefore.Should().Be(1);
            result.TemplatesCreated.Should().Be(1);
            File.Exists(Path.Combine(MigrationLayout.PatchDir(stackRoot, startKey), "progression.json")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(buildsPath))
            {
                Directory.Delete(buildsPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RecreateMissingPatchesAsync_works_when_patches_already_applied()
    {
        var stackId = "ip-recreate-applied";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-ip-recreate-applied-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        try
        {
            await using var db = CreateDbContext(new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
                ModuleIdsJson = """["mod-individual-progression"]""",
            });
            var service = CreateSyncService(db, buildsPath);

            await service.BootstrapAsync(stackId);
            SeedTestProgressionRepo(stackRoot);

            var stack = await db.ManagedStacks.SingleAsync(s => s.Id == stackId);
            stack.AppliedPatchLevel = 1_002_000;
            await db.SaveChangesAsync();

            SeedStackPatchesFromRepo(stackRoot);
            var startKey = PatchFolderNames.Format(new PatchIndex(1, 0, explicitSub1: true), "Start");
            Directory.Delete(MigrationLayout.PatchDir(stackRoot, startKey), recursive: true);

            var result = await service.RecreateMissingPatchesAsync(stackId);

            result.TemplatesCreated.Should().Be(1);
            File.Exists(Path.Combine(MigrationLayout.PatchDir(stackRoot, startKey), "progression.json")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(buildsPath))
            {
                Directory.Delete(buildsPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RecreateMissingPatchesAsync_removes_legacy_placeholder_patches()
    {
        var stackId = "ip-recreate-legacy";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-ip-recreate-legacy-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        try
        {
            await using var db = CreateDbContext(new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
                ModuleIdsJson = """["mod-individual-progression"]""",
            });
            var service = CreateSyncService(db, buildsPath);

            await service.BootstrapAsync(stackId);
            SeedTestProgressionRepo(stackRoot);
            SeedStackPatchesFromRepo(stackRoot);
            MigrationLayout.EnsurePatchDirectories(stackRoot, "patch 1");
            MigrationLayout.EnsurePatchDirectories(stackRoot, "patch 2");
            MigrationLayout.EnsurePatchDirectories(stackRoot, "patch 3");

            await service.RecreateMissingPatchesAsync(stackId);

            Directory.Exists(MigrationLayout.PatchDir(stackRoot, "patch 1")).Should().BeFalse();
            Directory.Exists(MigrationLayout.PatchDir(stackRoot, "patch 2")).Should().BeFalse();
            Directory.Exists(MigrationLayout.PatchDir(stackRoot, "patch 3")).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(buildsPath))
            {
                Directory.Delete(buildsPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ValidatePatchesAsync_passes_with_no_patches_and_no_progression_repo()
    {
        var stackId = "ip-validate-empty";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-ip-validate-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var db = CreateDbContext(new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
                ModuleIdsJson = """["mod-individual-progression"]""",
                CoreCommitSha = "abc123",
                LastBuiltAt = DateTime.UtcNow,
            });
            var service = CreateSyncService(db, buildsPath);

            await service.BootstrapAsync(stackId);

            var stackRoot = Path.Combine(buildsPath, stackId);
            var migrationsRoot = MigrationLayout.MigrationsRoot(stackRoot);
            if (Directory.Exists(migrationsRoot))
            {
                Directory.Delete(migrationsRoot, recursive: true);
            }
            Directory.CreateDirectory(migrationsRoot);

            var result = await service.ValidatePatchesAsync(stackId);

            result.Passed.Should().BeTrue();
            result.Mode.Should().Be(PatchValidationMode.ConfigOnly);
            result.Errors.Should().BeEmpty();
            result.KeyChecks.Should().BeEmpty();
            result.PatchCount.Should().Be(0);
            result.ExpectedPatchCount.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(buildsPath))
            {
                Directory.Delete(buildsPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ValidatePatchesAsync_config_only_without_mip_module()
    {
        var stackId = "validate-no-mip";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-validate-no-mip-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        try
        {
            await using var db = CreateDbContext(new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
                ModuleIdsJson = "[]",
            });
            var service = CreateSyncService(db, buildsPath);

            var patchKey = "patch 1.0 Test";
            var configDir = MigrationLayout.ConfigDir(stackRoot, patchKey);
            Directory.CreateDirectory(configDir);
            await File.WriteAllTextAsync(Path.Combine(configDir, "worldserver.json"), """{"SomeKey":"1"}""");

            var etcDir = MigrationLayout.EtcDir(stackRoot);
            Directory.CreateDirectory(etcDir);
            await File.WriteAllTextAsync(Path.Combine(etcDir, "worldserver.conf"), "SomeKey = 0\n");

            var serverConfig = new Mock<IServerConfigService>();
            serverConfig
                .Setup(s => s.ReadAsync(stackId, "worldserver.conf", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ServerConfigContentDto { Content = "SomeKey = 0\n" });

            var docker = Options.Create(new DockerOptions { BuildsPath = buildsPath });
            var migrations = Options.Create(new MigrationOptions());
            var httpClientFactory = new Mock<IHttpClientFactory>();
            var serviceWithConfig = new IndividualProgressionSyncService(
                db,
                serverConfig.Object,
                httpClientFactory.Object,
                docker,
                migrations,
                NullLogger<IndividualProgressionSyncService>.Instance);

            var result = await serviceWithConfig.ValidatePatchesAsync(stackId);

            result.Mode.Should().Be(PatchValidationMode.ConfigOnly);
            result.Passed.Should().BeTrue();
            result.KeyChecks.Should().ContainSingle(check => check.Key == "SomeKey");
        }
        finally
        {
            if (Directory.Exists(buildsPath))
            {
                Directory.Delete(buildsPath, recursive: true);
            }
        }
    }

    private static void SeedTestProgressionRepo(string stackRoot)
    {
        foreach (var definition in IndividualProgressionPatchCatalog.All)
        {
            var expansion = definition.Expansion switch
            {
                "classic" => "Classic",
                "tbc" => "Tbc",
                "wotlk" => "Wotlk",
                _ => "Classic",
            };
            var patchFolder = $"{definition.Index} {RepoLabel(definition)}";
            Directory.CreateDirectory(Path.Combine(MigrationLayout.ProgressionRepoDir(stackRoot), expansion, patchFolder));
        }
    }

    private static string RepoLabel(ProgressionPatchDefinition definition) => definition.Index switch
    {
        "1.0" => "Start",
        "2.1" => "Serpentshrine Cavern",
        "3.5" => "Ruby Sanctum",
        _ => definition.Slug.Replace('_', ' '),
    };

    private static void SeedStackPatchesFromRepo(string stackRoot)
    {
        ProgressionRepoPatchSeeder.Seed(
            MigrationLayout.ProgressionRepoDir(stackRoot),
            stackRoot,
            onlyMissing: false);
    }

    private static IndividualProgressionSyncService CreateSyncService(AzerothCoreDbContext db, string buildsPath)
    {
        var docker = Options.Create(new DockerOptions { BuildsPath = buildsPath });
        var migrations = Options.Create(new MigrationOptions());
        var serverConfig = new Mock<IServerConfigService>();
        const string moduleConf = """
            IndividualProgression.StartingProgression = 1
            IndividualProgression.ProgressionLimit = 1
            IndividualProgression.TbcRacesUnlockProgression = 8
            IndividualProgression.TbcRacesStartingProgression = 8
            """;
        const string worldConf = "Expansion = 0\n";
        serverConfig
            .Setup(s => s.ReadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string path, CancellationToken _) => new ServerConfigContentDto
            {
                Content = path.Contains("worldserver", StringComparison.OrdinalIgnoreCase) ? worldConf : moduleConf,
            });
        serverConfig
            .Setup(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerConfigListDto());

        var httpClientFactory = new Mock<IHttpClientFactory>();

        return new IndividualProgressionSyncService(
            db,
            serverConfig.Object,
            httpClientFactory.Object,
            docker,
            migrations,
            NullLogger<IndividualProgressionSyncService>.Instance);
    }

    private static AzerothCoreDbContext CreateDbContext(ManagedStackEntity stack)
    {
        var options = new DbContextOptionsBuilder<AzerothCoreDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var db = new AzerothCoreDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        db.ManagedStacks.Add(stack);
        db.SaveChanges();
        return db;
    }
}

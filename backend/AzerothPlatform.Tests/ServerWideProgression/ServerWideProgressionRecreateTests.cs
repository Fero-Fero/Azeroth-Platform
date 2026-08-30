using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services.ServerWideProgression;
using AzerothPlatform.Infrastructure.Services.Patches;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AzerothPlatform.Tests.ServerWideProgression;

public sealed class ServerWideProgressionRecreateTests
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
    public async Task RecreateMissingPatchesAsync_removes_placeholder_patches()
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
            MigrationLayout.EnsurePatchDirectories(stackRoot, "patch 1.0");
            MigrationLayout.EnsurePatchDirectories(stackRoot, "patch 2.0");
            MigrationLayout.EnsurePatchDirectories(stackRoot, "patch 3.0");

            await service.RecreateMissingPatchesAsync(stackId);

            Directory.Exists(MigrationLayout.PatchDir(stackRoot, "patch 1.0")).Should().BeFalse();
            Directory.Exists(MigrationLayout.PatchDir(stackRoot, "patch 2.0")).Should().BeFalse();
            Directory.Exists(MigrationLayout.PatchDir(stackRoot, "patch 3.0")).Should().BeFalse();
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

            result.Passed.Should().BeFalse();
            result.Mode.Should().Be(PatchValidationMode.ConfigOnly);
            result.Errors.Should().Contain(error =>
                error.Contains("Progression sync has not completed", StringComparison.Ordinal));
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
            var serviceWithConfig = new ServerWideProgressionService(
                db,
                serverConfig.Object,
                httpClientFactory.Object,
                docker,
                migrations,
                NullLogger<ServerWideProgressionService>.Instance);

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

    [Fact]
    public async Task ValidatePatchesAsync_fails_when_patch_folders_use_catalog_slugs()
    {
        var stackId = "ip-validate-misnamed";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-ip-validate-misnamed-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
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
            SeedRealisticProgressionRepo(stackRoot);
            SeedCompletedSyncArtifacts(stackRoot, pruneRepo: true);

            var startKey = PatchFolderNames.Format(new PatchIndex(1, 0, explicitSub1: true), "Start");
            var onyxiaKey = PatchFolderNames.Format(new PatchIndex(1, 2, explicitSub1: true), "ONYXIA");
            MigrationLayout.EnsurePatchDirectories(stackRoot, startKey);
            MigrationLayout.EnsurePatchDirectories(stackRoot, onyxiaKey);
            File.WriteAllText(Path.Combine(MigrationLayout.PatchDir(stackRoot, startKey), "description.md"), "Start");
            File.WriteAllText(Path.Combine(MigrationLayout.PatchDir(stackRoot, onyxiaKey), "description.md"), "Onyxia");
            File.WriteAllText(Path.Combine(MigrationLayout.PatchDir(stackRoot, onyxiaKey), "progression.json"), "{}");

            var result = await service.ValidatePatchesAsync(stackId);

            result.Passed.Should().BeFalse();
            result.Mode.Should().Be(PatchValidationMode.Full);
            result.ExpectedPatchCount.Should().BeGreaterThan(2);
            result.Errors.Should().Contain(error => error.Contains("Missing", StringComparison.Ordinal));
            result.Errors.Should().Contain(error => error.Contains("ONYXIA", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(buildsPath))
            {
                Directory.Delete(buildsPath, recursive: true);
            }
        }
    }

    private static void SeedRealisticProgressionRepo(string stackRoot)
    {
        var repoRoot = MigrationLayout.ProgressionRepoDir(stackRoot);
        foreach (var (expansion, patchFolder) in new (string, string)[]
                 {
                     ("Classic", "1.0 Start"),
                     ("Classic", "1.1 Molten Core & Onyxia"),
                     ("Classic", "1.2 Blackwing Lair"),
                     ("Classic", "1.3 Pre AQ"),
                     ("Tbc", "2.0 Karazhan, Gruul's Lair, Magtheridon's Lair"),
                     ("Wotlk", "3.0 Naxxramas, Eye of Eternity, Obsidian Sanctum"),
                 })
        {
            var patchDir = Path.Combine(repoRoot, expansion, patchFolder);
            Directory.CreateDirectory(Path.Combine(patchDir, "config"));
            File.WriteAllText(Path.Combine(patchDir, "description.md"), patchFolder);
        }
    }

    [Fact]
    public async Task ValidatePatchesAsync_fails_when_only_subset_of_repo_patches_present()
    {
        var stackId = "ip-validate-subset";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-ip-validate-subset-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
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
            SeedTestProgressionRepo(stackRoot);
            SeedCompletedSyncArtifacts(stackRoot, pruneRepo: true);

            // Labels must match SeedTestProgressionRepo (catalog slugs) so Directory.Exists
            // counts both folders on case-sensitive filesystems used by CI.
            var startKey = PatchFolderNames.Format(new PatchIndex(1, 0, explicitSub1: true), "Start");
            var onyxiaKey = PatchFolderNames.Format(new PatchIndex(1, 2, explicitSub1: true), "ONYXIA");
            MigrationLayout.EnsurePatchDirectories(stackRoot, startKey);
            MigrationLayout.EnsurePatchDirectories(stackRoot, onyxiaKey);
            File.WriteAllText(Path.Combine(MigrationLayout.PatchDir(stackRoot, startKey), "description.md"), "Start");
            File.WriteAllText(Path.Combine(MigrationLayout.PatchDir(stackRoot, onyxiaKey), "description.md"), "Onyxia");

            var result = await service.ValidatePatchesAsync(stackId);

            result.Passed.Should().BeFalse();
            result.Mode.Should().Be(PatchValidationMode.Full);
            result.ExpectedPatchCount.Should().BeGreaterThan(2);
            result.PatchCount.Should().Be(2);
            result.Errors.Should().Contain(error => error.Contains("Missing", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(buildsPath))
            {
                Directory.Delete(buildsPath, recursive: true);
            }
        }
    }

    private static void SeedCompletedSyncArtifacts(string stackRoot, bool pruneRepo = false)
    {
        var repoPath = MigrationLayout.ProgressionRepoDir(stackRoot);
        var manifest = ProgressionReferenceManifestBuilder.BuildFromRepo(repoPath);
        var syncLog = new ProgressionOptionalFilesLogDto
        {
            LastSyncAt = DateTimeOffset.UtcNow,
            LastKnownPatchKeys = manifest.ExpectedPatchKeys,
        };

        File.WriteAllText(
            Path.Combine(stackRoot, "progression_sync_log.json"),
            JsonSerializer.Serialize(syncLog, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(
            Path.Combine(stackRoot, "progression_reference_manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        if (pruneRepo && Directory.Exists(repoPath))
        {
            Directory.Delete(repoPath, recursive: true);
        }
    }

    private static void SeedTestProgressionRepo(string stackRoot)
    {
        foreach (var definition in ServerWideProgressionPatchCatalog.All)
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

    private static ServerWideProgressionService CreateSyncService(AzerothCoreDbContext db, string buildsPath)
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

        return new ServerWideProgressionService(
            db,
            serverConfig.Object,
            httpClientFactory.Object,
            docker,
            migrations,
            NullLogger<ServerWideProgressionService>.Instance);
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

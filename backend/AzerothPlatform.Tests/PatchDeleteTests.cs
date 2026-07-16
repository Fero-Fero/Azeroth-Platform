using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services;
using AzerothPlatform.Infrastructure.Services.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class PatchDeleteTests
{
    [Fact]
    public async Task DeletePatchEntryAsync_removes_unapplied_patch_folder_when_level_zero()
    {
        var stackId = "delete-patch";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-delete-patch-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        var patchKey = "patch 1.0";
        try
        {
            MigrationLayout.EnsureDefaultPatches(stackRoot);
            Directory.Exists(MigrationLayout.PatchDir(stackRoot, patchKey)).Should().BeTrue();

            await using var db = CreateDbContext(new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
                ModuleIdsJson = "[]",
            });
            var service = CreateMigrationService(db, buildsPath);

            await service.DeletePatchEntryAsync(stackId, patchKey);

            Directory.Exists(MigrationLayout.PatchDir(stackRoot, patchKey)).Should().BeFalse();
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
    public async Task GetOverviewAsync_does_not_recreate_default_placeholders_after_delete()
    {
        var stackId = "overview-no-recreate";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-overview-recreate-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        var patchKey = "patch 2.0";
        try
        {
            MigrationLayout.EnsureDefaultPatches(stackRoot);
            Directory.Delete(MigrationLayout.PatchDir(stackRoot, patchKey), recursive: true);
            Directory.Exists(MigrationLayout.PatchDir(stackRoot, patchKey)).Should().BeFalse();

            await using var db = CreateDbContext(new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
                ModuleIdsJson = "[]",
            });
            var service = CreateMigrationService(db, buildsPath);

            await service.GetOverviewAsync(stackId);

            Directory.Exists(MigrationLayout.PatchDir(stackRoot, patchKey)).Should().BeFalse();
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
    public async Task DeleteAllPatchesAsync_removes_all_patch_folders_when_none_applied()
    {
        var stackId = "drop-all-patches";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-drop-all-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        try
        {
            MigrationLayout.EnsureDefaultPatches(stackRoot);
            Directory.CreateDirectory(MigrationLayout.PatchDir(stackRoot, "patch 3.0"));

            await using var db = CreateDbContext(new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
                ModuleIdsJson = "[]",
            });
            var service = CreateMigrationService(db, buildsPath);

            var deleted = await service.DeleteAllPatchesAsync(stackId);

            deleted.Should().BeGreaterThan(0);
            Directory.EnumerateDirectories(MigrationLayout.MigrationsRoot(stackRoot)).Should().BeEmpty();
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
    public async Task DeleteAllPatchesAsync_throws_when_any_patch_has_been_applied()
    {
        var stackId = "drop-all-applied";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-drop-all-applied-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        try
        {
            MigrationLayout.EnsureDefaultPatches(stackRoot);

            await using var db = CreateDbContext(new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 1,
                ModuleIdsJson = "[]",
            });
            var service = CreateMigrationService(db, buildsPath);

            var act = () => service.DeleteAllPatchesAsync(stackId);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Cannot drop all patches*");
        }
        finally
        {
            if (Directory.Exists(buildsPath))
            {
                Directory.Delete(buildsPath, recursive: true);
            }
        }
    }

    private static MigrationService CreateMigrationService(AzerothCoreDbContext db, string buildsPath)
    {
        var docker = Options.Create(new DockerOptions { BuildsPath = buildsPath });
        var migration = Options.Create(new MigrationOptions());
        var clientServer = Options.Create(new ClientServerOptions());
        var clientDistribution = new Mock<IClientDistributionService>();
        var imageService = new Mock<IMigrationImageService>();
        var remoteEngine = new Mock<IRemoteEngineService>();
        var ipSync = new Mock<IIndividualProgressionSyncService>();
        ipSync.Setup(s => s.StackHasModule(It.IsAny<IReadOnlyList<string>>())).Returns(false);

        return new MigrationService(
            db,
            docker,
            migration,
            clientDistribution.Object,
            imageService.Object,
            remoteEngine.Object,
            ipSync.Object,
            clientServer,
            NullLogger<MigrationService>.Instance);
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

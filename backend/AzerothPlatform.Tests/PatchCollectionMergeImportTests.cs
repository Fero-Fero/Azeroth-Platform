using System.IO.Compression;
using System.Text;
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

public sealed class PatchCollectionMergeImportTests
{
    [Fact]
    public async Task ImportPatchCollectionAsync_merge_maps_flat_classic_archive_onto_template_names()
    {
        var stackId = "merge-collection-flat-classic";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-merge-collection-flat-classic-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        var templateKey = "patch 1.0 START";
        try
        {
            MigrationLayout.EnsurePatchDirectories(stackRoot, templateKey);

            var stack = new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
            };

            await using var db = CreateDbContext(stack);
            var service = CreateMigrationService(db, buildsPath);

            await using var archive = CreateZip(
                ("patch 1.0/sql/world/update.sql", "SELECT 1;"),
                ("patch 1.1/mpq/patch-a.MPQ", "MPQ"));

            var result = await service.ImportPatchCollectionAsync(stackId, archive, "merge");

            result.ImportedCount.Should().Be(2);
            result.ImportedPatches.Should().Contain(p =>
                p.SourceKey == "patch 1.0" && p.TargetKey == templateKey);
            result.ImportedPatches.Should().Contain(p =>
                p.SourceKey == "patch 1.1" && p.TargetKey == "patch 1.1");
            File.Exists(Path.Combine(MigrationLayout.SqlDir(stackRoot, templateKey), "world", "update.sql")).Should().BeTrue();
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
    public async Task ImportPatchCollectionAsync_merge_maps_flat_tbc_and_wotlk_archives()
    {
        var stackId = "merge-collection-flat-expansions";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-merge-collection-flat-expansions-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        var tbcTemplateKey = "patch 2.0 PRE_TBC";
        var wotlkTemplateKey = "patch 3.0 WOTLK_TIER_1";
        try
        {
            MigrationLayout.EnsurePatchDirectories(stackRoot, tbcTemplateKey);
            MigrationLayout.EnsurePatchDirectories(stackRoot, wotlkTemplateKey);

            var stack = new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
            };

            await using var db = CreateDbContext(stack);
            var service = CreateMigrationService(db, buildsPath);

            await using var tbcArchive = CreateZip(
                ("patch 2.0/sql/world/pre_tbc.sql", "SELECT 1;"));
            await using var wotlkArchive = CreateZip(
                ("patch 3.0/sql/world/wotlk_tier_1.sql", "SELECT 2;"));

            var tbcResult = await service.ImportPatchCollectionAsync(stackId, tbcArchive, "merge");
            var wotlkResult = await service.ImportPatchCollectionAsync(stackId, wotlkArchive, "merge");

            tbcResult.ImportedPatches[0].TargetKey.Should().Be(tbcTemplateKey);
            wotlkResult.ImportedPatches[0].TargetKey.Should().Be(wotlkTemplateKey);
            File.Exists(Path.Combine(MigrationLayout.SqlDir(stackRoot, tbcTemplateKey), "world", "pre_tbc.sql")).Should().BeTrue();
            File.Exists(Path.Combine(MigrationLayout.SqlDir(stackRoot, wotlkTemplateKey), "world", "wotlk_tier_1.sql")).Should().BeTrue();
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
    public async Task ImportPatchCollectionAsync_merge_maps_index_only_archive_folders_onto_template_names()
    {
        var stackId = "merge-collection-index";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-merge-collection-index-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        var templateKey = "patch 1.0 START";
        try
        {
            MigrationLayout.EnsurePatchDirectories(stackRoot, templateKey);

            var stack = new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
            };

            await using var db = CreateDbContext(stack);
            var service = CreateMigrationService(db, buildsPath);

            await using var archive = CreateZip(
                ("Server Wide Progression Preset/classic/patch 1.0/sql/world/update.sql", "SELECT 1;"),
                ("Server Wide Progression Preset/classic/patch 1.1/mpq/patch-a.MPQ", "MPQ"));

            var result = await service.ImportPatchCollectionAsync(stackId, archive, "merge");

            result.ImportedCount.Should().Be(2);
            result.ImportedPatches.Should().Contain(p =>
                p.SourceKey == "patch 1.0" && p.TargetKey == templateKey);
            result.ImportedPatches.Should().Contain(p =>
                p.SourceKey == "patch 1.1" && p.TargetKey == "patch 1.1");
            File.Exists(Path.Combine(MigrationLayout.SqlDir(stackRoot, templateKey), "world", "update.sql")).Should().BeTrue();
            Directory.Exists(Path.Combine(MigrationLayout.MigrationsRoot(stackRoot), "patch 1.0")).Should().BeFalse();
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
    public async Task ImportPatchCollectionAsync_merge_maps_patch_1_1_onto_molten_core_template()
    {
        var stackId = "merge-collection-mc";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-merge-collection-mc-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        var templateKey = "patch 1.1 MOLTEN_CORE";
        try
        {
            MigrationLayout.EnsurePatchDirectories(stackRoot, templateKey);

            var stack = new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
            };

            await using var db = CreateDbContext(stack);
            var service = CreateMigrationService(db, buildsPath);

            await using var archive = CreateZip(
                ("classic/patch 1.1/sql/world/molten_core.sql", "SELECT 1;"));

            var result = await service.ImportPatchCollectionAsync(stackId, archive, "merge");

            result.ImportedPatches[0].TargetKey.Should().Be(templateKey);
            File.Exists(Path.Combine(MigrationLayout.SqlDir(stackRoot, templateKey), "world", "molten_core.sql")).Should().BeTrue();
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
    public async Task ImportPatchCollectionAsync_merge_copies_files_into_existing_patch_folders()
    {
        var stackId = "merge-collection";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-merge-collection-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        var patchKey = "patch 1.0 START";
        try
        {
            MigrationLayout.EnsurePatchDirectories(stackRoot, patchKey);
            await File.WriteAllTextAsync(
                Path.Combine(MigrationLayout.SqlDir(stackRoot, patchKey), "world", "placeholder.sql"),
                "-- placeholder");

            var stack = new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
            };

            await using var db = CreateDbContext(stack);
            var service = CreateMigrationService(db, buildsPath);

            await using var archive = CreateZip(
                ("classic/patch 1.0 START/sql/world/update.sql", "SELECT 1;"),
                ("classic/patch 1.0 START/mpq/patch-a.MPQ", "MPQ"));

            var result = await service.ImportPatchCollectionAsync(stackId, archive, "merge");

            result.Mode.Should().Be("merge");
            result.ImportedCount.Should().Be(1);
            result.ImportedPatches[0].TargetKey.Should().Be(patchKey);
            File.Exists(Path.Combine(MigrationLayout.SqlDir(stackRoot, patchKey), "world", "update.sql")).Should().BeTrue();
            File.Exists(Path.Combine(MigrationLayout.SqlDir(stackRoot, patchKey), "world", "placeholder.sql")).Should().BeTrue();
            File.Exists(Path.Combine(MigrationLayout.MpqDir(stackRoot, patchKey), "patch-a.MPQ")).Should().BeTrue();
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
    public async Task ImportPatchCollectionAsync_merge_creates_missing_patch_folders()
    {
        var stackId = "merge-collection-new";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-merge-collection-new-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        try
        {
            var stack = new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
            };

            await using var db = CreateDbContext(stack);
            var service = CreateMigrationService(db, buildsPath);

            await using var archive = CreateZip(
                ("classic/patch 1.1 MOLTEN_CORE/sql/world/update.sql", "SELECT 2;"));

            var result = await service.ImportPatchCollectionAsync(stackId, archive, "merge");

            result.ImportedCount.Should().Be(1);
            var patchKey = result.ImportedPatches[0].TargetKey;
            patchKey.Should().Be("patch 1.1 MOLTEN_CORE");
            File.Exists(Path.Combine(MigrationLayout.SqlDir(stackRoot, patchKey), "world", "update.sql")).Should().BeTrue();
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
    public async Task ImportPatchCollectionAsync_merge_rejects_applied_patch()
    {
        var stackId = "merge-collection-applied";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-merge-collection-applied-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        var patchKey = "patch 1.0 START";
        try
        {
            MigrationLayout.EnsurePatchDirectories(stackRoot, patchKey);
            var stack = new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 1_000_000,
            };

            await using var db = CreateDbContext(stack);
            var service = CreateMigrationService(db, buildsPath);

            await using var archive = CreateZip(
                ("classic/patch 1.0 START/sql/world/update.sql", "SELECT 1;"));

            var act = () => service.ImportPatchCollectionAsync(stackId, archive, "merge");
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*already-applied*");
        }
        finally
        {
            if (Directory.Exists(buildsPath))
            {
                Directory.Delete(buildsPath, recursive: true);
            }
        }
    }

    private static MemoryStream CreateZip(params (string path, string content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
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

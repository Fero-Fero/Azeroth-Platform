using System.IO.Compression;
using System.Text;
using AzerothPlatform.Core.Contracts;
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

public sealed class MergePatchImportTests
{
    [Fact]
    public async Task MergePatchImportAsync_copies_sql_and_mpq_into_target_patch()
    {
        var stackId = "merge-test";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-merge-test-" + Guid.NewGuid().ToString("N"));
        var stackRoot = Path.Combine(buildsPath, stackId);
        var patchKey = "patch 1.1 TEST";
        try
        {
            MigrationLayout.EnsurePatchDirectories(stackRoot, patchKey);

            var stack = new ManagedStackEntity
            {
                Id = stackId,
                StackName = stackId,
                AppliedPatchLevel = 0,
            };

            await using var db = CreateDbContext(stack);
            var service = CreateMigrationService(db, buildsPath);

            await using var sqlStream = CreateZip(("sql/world/update.sql", "SELECT 1;"));
            await using var clientStream = CreateZip(("mpq/patch-a.MPQ", "MPQ"));

            var result = await service.MergePatchImportAsync(stackId, patchKey, sqlStream, clientStream);

            result.SqlFiles.Should().Be(1);
            result.MpqFiles.Should().Be(1);
            File.Exists(Path.Combine(MigrationLayout.SqlDir(stackRoot, patchKey), "world", "update.sql")).Should().BeTrue();
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
    public async Task MergePatchImportAsync_rejects_applied_patch()
    {
        var stackId = "merge-applied";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-merge-applied-" + Guid.NewGuid().ToString("N"));
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
            await using var sqlStream = CreateZip(("sql/world/update.sql", "SELECT 1;"));

            var act = () => service.MergePatchImportAsync(stackId, patchKey, sqlStream, null);
            await act.Should().ThrowAsync<InvalidOperationException>()
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

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

public sealed class MpqRemovalImportTests
{
    [Theory]
    [InlineData("""{"remove": "Patch-L.MPQ"}""", "Patch-L.MPQ")]
    [InlineData("""{"REMOVE": "patch-l.mpq"}""", "patch-l.mpq")]
    [InlineData("""{"remove": ["Patch-L.MPQ", "patch-B.MPQ"]}""", "Patch-L.MPQ", "patch-B.MPQ")]
    [InlineData("""["patch-a.MPQ", "patch-b.MPQ"]""", "patch-a.MPQ", "patch-b.MPQ")]
    public void TryParseMpqRemovalJson_parses_supported_instruction_formats(
        string json,
        params string[] expected)
    {
        MigrationService.TryParseMpqRemovalJson(json, out var removals).Should().BeTrue();
        removals.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    [Fact]
    public void TryParseMpqRemovalJson_rejects_invalid_documents()
    {
        MigrationService.TryParseMpqRemovalJson("""{"other": "patch-a.MPQ"}""", out _).Should().BeFalse();
        MigrationService.TryParseMpqRemovalJson("not json", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ImportPatchCollectionAsync_imports_mpq_remove_json_into_remove_sidecar()
    {
        var stackId = "import-remove";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-import-remove-" + Guid.NewGuid().ToString("N"));
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
                ("classic/patch 1.1 TEST/mpq/remove.json", """{"remove": "Patch-L.MPQ"}"""));

            var result = await service.ImportPatchCollectionAsync(stackId, archive, "append");

            result.ImportedCount.Should().Be(1);
            var patchKey = result.ImportedPatches[0].TargetKey;
            MigrationService.ReadMpqRemovals(stackRoot, patchKey)
                .Should().ContainSingle(name => name.Equals("Patch-L.MPQ", StringComparison.OrdinalIgnoreCase));
            File.Exists(Path.Combine(MigrationLayout.MpqDir(stackRoot, patchKey), "remove.json")).Should().BeFalse();
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
    public async Task MergePatchImportAsync_imports_mpq_remove_json_into_remove_sidecar()
    {
        var stackId = "merge-remove";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-merge-remove-" + Guid.NewGuid().ToString("N"));
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

            await using var clientStream = CreateZip(
                ("mpq/remove.json", """{"remove": "patch-l.mpq"}"""),
                ("mpq/patch-a.MPQ", "MPQ"));

            var result = await service.MergePatchImportAsync(stackId, patchKey, null, clientStream);

            result.MpqFiles.Should().Be(2);
            MigrationService.ReadMpqRemovals(stackRoot, patchKey)
                .Should().ContainSingle(name => name.Equals("patch-l.mpq", StringComparison.OrdinalIgnoreCase));
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

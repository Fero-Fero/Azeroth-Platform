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
        var serverConfig = new Mock<IServerConfigService>();
        var stackRegistry = new Mock<IStackRegistryService>();

        return new MigrationService(
            db,
            docker,
            migration,
            clientDistribution.Object,
            imageService.Object,
            remoteEngine.Object,
            ipSync.Object,
            serverConfig.Object,
            stackRegistry.Object,
            new Mock<ILauncherPortalService>().Object,
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

public sealed class MpqManifestReaderTests
{
    private const string ProgressionTemplate = """
        // example: { 
        //   "add": "Patch-W.MPQ", 
        //   "remove": "Patch-W.MPQ" 
        //   "description": {
        //        "Patch-W.MPQ": "This is a description of the patch in English",
        //    }
        // }
        """;

    [Fact]
    public void Parse_comment_only_template_returns_empty_manifest()
    {
        var manifest = MpqManifestReader.Parse(ProgressionTemplate);
        manifest.Should().NotBeNull();
        manifest!.Add.Should().BeEmpty();
        manifest.Remove.Should().BeEmpty();
        manifest.Description.Should().BeEmpty();
    }

    [Fact]
    public void Parse_remove_only_manifest()
    {
        var manifest = MpqManifestReader.Parse("""{"remove":["patch-w.mpq"]}""");
        manifest.Should().NotBeNull();
        manifest!.Remove.Should().ContainSingle("patch-w.mpq");
        manifest.Add.Should().BeEmpty();
    }

    [Fact]
    public void Parse_prebuilt_description_only_manifest()
    {
        var manifest = MpqManifestReader.Parse("""
            {
              "description": { "patch-k.mpq": "Onyxia client changes" }
            }
            """);
        manifest.Should().NotBeNull();
        manifest!.Add.Should().BeEmpty();
        manifest.Description["patch-k.mpq"].Should().Be("Onyxia client changes");
    }

    [Fact]
    public void Parse_prebuilt_with_description_and_add_for_construction()
    {
        var manifest = MpqManifestReader.Parse("""
            {
              "add": ["patch-k.mpq"],
              "description": { "patch-k.mpq": "Onyxia client changes" }
            }
            """);
        manifest.Should().NotBeNull();
        manifest!.Add.Should().ContainSingle("patch-k.mpq");
        manifest.Description["patch-k.mpq"].Should().Be("Onyxia client changes");
    }

    [Fact]
    public void CollectMpqRemovals_merges_manifest_and_legacy_remove_json()
    {
        var stackRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var patchKey = "patch 1.2 TEST";
        try
        {
            MigrationLayout.EnsurePatchDirectories(stackRoot, patchKey);
            File.WriteAllText(
                Path.Combine(MigrationLayout.MpqDir(stackRoot, patchKey), "mpq.json"),
                """{"remove":["patch-w.mpq"]}""");
            File.WriteAllText(
                Path.Combine(MigrationLayout.MpqDir(stackRoot, patchKey), MigrationService.MpqRemovalsFileName),
                """["patch-x.mpq"]""");

            var removals = MigrationService.CollectMpqRemovals(stackRoot, patchKey);
            removals.Should().BeEquivalentTo(["patch-w.mpq", "patch-x.mpq"]);
        }
        finally
        {
            if (Directory.Exists(stackRoot))
            {
                Directory.Delete(stackRoot, recursive: true);
            }
        }
    }
}

public sealed class MpqPackFilterTests
{
    [Theory]
    [InlineData("mpq.json", false)]
    [InlineData("MPQ.JSON", false)]
    [InlineData("remove.json", false)]
    [InlineData(".remove.json", false)]
    [InlineData("patch-k.mpq", false)]
    [InlineData("patch-k.mpq.desc", false)]
    [InlineData("other.json", false)]
    [InlineData("Interface/GLUES/foo.blp", true)]
    [InlineData("Interface/mpq.json", false)]
    public void ShouldIncludeInConstructedMpq_excludes_manifests_and_sidecars(string path, bool expected)
    {
        MpqPackFilter.ShouldIncludeInConstructedMpq(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("patch-k.mpq", true)]
    [InlineData("Patch-W.MPQ", true)]
    [InlineData("mpq.json", false)]
    [InlineData("readme.txt", false)]
    [InlineData("", false)]
    public void IsValidConstructedMpqName_requires_mpq_extension(string name, bool expected)
    {
        MpqPackFilter.IsValidConstructedMpqName(name).Should().Be(expected);
    }
}

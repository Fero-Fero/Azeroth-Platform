using System.IO.Compression;
using System.Text;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Core.Services.Interfaces;
using AzerothPlatform.Infrastructure.Configuration;
using AzerothPlatform.Infrastructure.Data;
using AzerothPlatform.Infrastructure.Data.Entities;
using AzerothPlatform.Infrastructure.Services;
using AzerothPlatform.Infrastructure.Services.Patches;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AzerothPlatform.Tests.Migrations;

public sealed class MpqRemovalImportTests
{
    [Fact]
    public async Task ImportPatchCollectionAsync_imports_mpq_json_remove_array()
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
                ("classic/patch 1.1 TEST/mpq/mpq.json", """{"remove": ["Patch-L.MPQ"]}"""));

            var result = await service.ImportPatchCollectionAsync(stackId, archive, "append");

            result.ImportedCount.Should().Be(1);
            var patchKey = result.ImportedPatches[0].TargetKey;
            MigrationService.CollectMpqRemovals(stackRoot, patchKey)
                .Should().ContainSingle(name => name.Equals("Patch-L.MPQ", StringComparison.OrdinalIgnoreCase));
            File.Exists(Path.Combine(MigrationLayout.MpqDir(stackRoot, patchKey), "mpq.json")).Should().BeTrue();
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
    public async Task ImportPatchCollectionAsync_skips_remove_json_sidecars()
    {
        var stackId = "import-skip-remove";
        var buildsPath = Path.Combine(Path.GetTempPath(), "azp-import-skip-remove-" + Guid.NewGuid().ToString("N"));
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
                ("classic/patch 1.1 TEST/mpq/remove.json", """["Patch-L.MPQ"]"""),
                ("classic/patch 1.1 TEST/mpq/Patch-Z.MPQ", "mpq"));

            var result = await service.ImportPatchCollectionAsync(stackId, archive, "append");

            result.ImportedCount.Should().Be(1);
            var patchKey = result.ImportedPatches[0].TargetKey;
            MigrationService.CollectMpqRemovals(stackRoot, patchKey).Should().BeEmpty();
            File.Exists(Path.Combine(MigrationLayout.MpqDir(stackRoot, patchKey), "remove.json")).Should().BeFalse();
            File.Exists(Path.Combine(MigrationLayout.MpqDir(stackRoot, patchKey), "Patch-Z.MPQ")).Should().BeTrue();
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
        var ipSync = new Mock<IServerWideProgressionService>();
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
    public void Parse_progression_string_add_and_remove()
    {
        var manifest = MpqManifestReader.Parse("""
            {
              "add": "Patch-W.MPQ",
              "remove": "Patch-W.MPQ",
              "description": {
                "Patch-W.MPQ": "Restore Classic UI, Maps, Textures"
              }
            }
            """);
        manifest.Should().NotBeNull();
        manifest!.Add.Should().Equal("Patch-W.MPQ");
        manifest.Remove.Should().Equal("Patch-W.MPQ");
        manifest.Description["Patch-W.MPQ"].Should().Be("Restore Classic UI, Maps, Textures");
        MpqPackFilter.ConstructedArchiveNames(manifest).Should().Equal("Patch-W.MPQ");
    }

    [Fact]
    public void ConstructedArchiveNames_ignores_interface_world_sound()
    {
        var manifest = new MpqManifestDto
        {
            Add = ["Interface.MPQ", "World.MPQ", "Sound.MPQ", "Patch-W.MPQ"],
        };
        MpqPackFilter.ConstructedArchiveNames(manifest).Should().Equal("Patch-W.MPQ");
    }

    [Theory]
    [InlineData("Interface.MPQ")]
    [InlineData("World.mpq")]
    [InlineData("sound.MPQ")]
    public void IsWowContentFolderArchive_rejects_stock_content_trees(string name)
        => MpqPackFilter.IsWowContentFolderArchive(name).Should().BeTrue();

    [Theory]
    [InlineData("Patch-W.MPQ")]
    [InlineData("patch-D.MPQ")]
    [InlineData("custom.mpq")]
    public void IsWowContentFolderArchive_allows_letter_and_custom_patches(string name)
        => MpqPackFilter.IsWowContentFolderArchive(name).Should().BeFalse();

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
    public void CollectMpqRemovals_reads_manifest_remove_only()
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
                Path.Combine(MigrationLayout.MpqDir(stackRoot, patchKey), "remove.json"),
                """["patch-x.mpq"]""");

            var removals = MigrationService.CollectMpqRemovals(stackRoot, patchKey);
            removals.Should().BeEquivalentTo(["patch-w.mpq"]);
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

    [Fact]
    public void HasPackableContentFor_is_true_when_named_folder_has_loose_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-mpq-pack-" + Guid.NewGuid().ToString("N"));
        var content = Path.Combine(root, "patch-K", "Data");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "foo.blp"), "x");
        File.WriteAllBytes(Path.Combine(root, "patch-K.MPQ"), [1, 2, 3]);
        try
        {
            MpqPackFilter.HasPackableContentFor(root, "patch-K.MPQ").Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ContentDirectoryFor_packs_loose_trees_and_excludes_sibling_mpqs()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-mpq-pack-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Interface", "GLUES"));
        Directory.CreateDirectory(Path.Combine(root, "World", "maps"));
        File.WriteAllText(Path.Combine(root, "Interface", "GLUES", "foo.blp"), "x");
        File.WriteAllText(Path.Combine(root, "World", "maps", "bar.adt"), "y");
        File.WriteAllBytes(Path.Combine(root, "Interface.MPQ"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "prebuilt.MPQ"), [4, 5, 6]);
        File.WriteAllText(Path.Combine(root, "mpq.json"), """{"add":["patch-A.MPQ"]}""");
        try
        {
            MpqPackFilter.ContentDirectoryFor(root, "patch-A.MPQ").Should().Be(root);
            MpqPackFilter.ContentDirectoryFor(root, "Interface.MPQ").Should().Be(root);
            var packed = MpqPackFilter.EnumeratePackableFiles(root)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .ToList();
            packed.Should().BeEquivalentTo(["Interface/GLUES/foo.blp", "World/maps/bar.adt"]);
            packed.Should().NotContain(p => p.EndsWith(".MPQ", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

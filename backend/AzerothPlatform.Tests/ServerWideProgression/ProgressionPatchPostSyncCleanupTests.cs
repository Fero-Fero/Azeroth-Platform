using System.IO.Compression;
using System.Text;
using AzerothPlatform.Infrastructure.Services.ServerWideProgression;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.ServerWideProgression;

public sealed class ProgressionPatchPostSyncCleanupTests
{
    [Fact]
    public void Extracts_zip_into_patch_folder_and_deletes_the_archive()
    {
        using var stack = new TempStack();
        var sqlDir = Path.Combine(stack.PatchDir, "sql", "world");
        Directory.CreateDirectory(sqlDir);
        File.WriteAllText(Path.Combine(stack.PatchDir, "progression.json"), "{}");

        var zipPath = Path.Combine(sqlDir, "scripts.zip");
        CreateZip(zipPath, ("creature.sql", "UPDATE creature SET spawntimesecs = 1;"));

        var log = new List<string>();
        var result = ProgressionPatchPostSyncCleanup.Run(stack.Root, log);

        result.ArchivesExtracted.Should().Be(1);
        File.Exists(zipPath).Should().BeFalse();
        File.ReadAllText(Path.Combine(sqlDir, "creature.sql")).Should().Contain("spawntimesecs");
        log.Should().Contain(entry => entry.Contains("scripts.zip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Strips_a_single_wrapper_folder_from_extracted_archives()
    {
        using var stack = new TempStack();
        Directory.CreateDirectory(stack.PatchDir);
        File.WriteAllText(Path.Combine(stack.PatchDir, "progression.json"), "{}");

        var zipPath = Path.Combine(stack.PatchDir, "bundle.zip");
        CreateZip(zipPath, ("wrapper/sql/world/item.sql", "UPDATE item_template SET stackable = 20;"));

        ProgressionPatchPostSyncCleanup.Run(stack.Root, new List<string>());

        File.Exists(zipPath).Should().BeFalse();
        File.Exists(Path.Combine(stack.PatchDir, "sql", "world", "item.sql")).Should().BeTrue();
    }

    [Fact]
    public void Leaves_custom_patches_without_progression_metadata_untouched()
    {
        using var stack = new TempStack(patchFolderName: "patch 4.0 custom");
        var sqlDir = Path.Combine(stack.PatchDir, "sql", "world");
        Directory.CreateDirectory(sqlDir);
        var zipPath = Path.Combine(sqlDir, "keep.zip");
        CreateZip(zipPath, ("keep.sql", "SELECT 1;"));

        var result = ProgressionPatchPostSyncCleanup.Run(stack.Root, new List<string>());

        result.ArchivesExtracted.Should().Be(0);
        File.Exists(zipPath).Should().BeTrue();
    }

    [Fact]
    public void Overwrites_existing_files_then_removes_the_zip()
    {
        using var stack = new TempStack();
        var sqlDir = Path.Combine(stack.PatchDir, "sql", "world");
        Directory.CreateDirectory(sqlDir);
        File.WriteAllText(Path.Combine(stack.PatchDir, "progression.json"), "{}");
        File.WriteAllText(Path.Combine(sqlDir, "creature.sql"), "OLD");

        var zipPath = Path.Combine(sqlDir, "creature.zip");
        CreateZip(zipPath, ("creature.sql", "NEW"));

        ProgressionPatchPostSyncCleanup.Run(stack.Root, new List<string>());

        File.Exists(zipPath).Should().BeFalse();
        File.ReadAllText(Path.Combine(sqlDir, "creature.sql")).Should().Be("NEW");
    }

    private static void CreateZip(string zipPath, params (string Entry, string Content)[] entries)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            var zipEntry = archive.CreateEntry(entry.Replace('\\', '/'));
            using var writer = new StreamWriter(zipEntry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }

    private sealed class TempStack : IDisposable
    {
        public TempStack(string patchFolderName = "patch 1.0 Start")
        {
            Root = Path.Combine(Path.GetTempPath(), "azp-patch-cleanup-" + Guid.NewGuid().ToString("N"));
            PatchDir = Path.Combine(Root, "migrations", patchFolderName);
            Directory.CreateDirectory(PatchDir);
        }

        public string Root { get; }

        public string PatchDir { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

using AzerothPlatform.ClientContent;
using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Client;

public sealed class ClientBaseMergePolicyTests
{
    [Theory]
    [InlineData("Data/common.MPQ", false)]
    [InlineData("Data/common-2.mpq", false)]
    [InlineData("Data/expansion.mpq", false)]
    [InlineData("Data/lichking.mpq", false)]
    [InlineData("Data/patch.mpq", false)]
    [InlineData("Data/patch-2.MPQ", false)]
    [InlineData("Data/patch-3.mpq", false)]
    [InlineData("Data/patch-D.MPQ", true)]
    [InlineData("Data/patch-K.mpq", true)]
    [InlineData("Data/Interface.MPQ", false)]
    [InlineData("Data/Sound.MPQ", false)]
    [InlineData("Data/World.MPQ", false)]
    [InlineData("Interface/AddOns/Foo/bar.lua", true)]
    [InlineData("Interface/AddOns", true)]
    [InlineData("Wow.exe", false)]
    [InlineData("Data/enUS/locale.mpq", false)]
    public void ShouldPreservePlatformContent_keeps_letter_patches_and_addons(string path, bool expected)
    {
        ClientBaseMergePolicy.ShouldPreservePlatformContent(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("Data/common.MPQ", true)]
    [InlineData("Data/common-2.mpq", true)]
    [InlineData("Data/expansion.mpq", true)]
    [InlineData("Data/lichking.mpq", true)]
    [InlineData("Data/patch.mpq", true)]
    [InlineData("Data/patch-2.MPQ", true)]
    [InlineData("Data/patch-3.mpq", true)]
    [InlineData("Data/Interface.MPQ", false)]
    [InlineData("Data/Sound.MPQ", false)]
    [InlineData("Data/World.MPQ", false)]
    [InlineData("Data/patch-D.MPQ", false)]
    [InlineData("Data/patch-K.mpq", false)]
    [InlineData("Wow.exe", false)]
    public void IsProtectedStockMpq_locks_only_the_default_archives(string path, bool expected)
    {
        ClientBaseMergePolicy.IsProtectedStockMpq(path).Should().Be(expected);
    }
}

public sealed class ClientBrowseMergerTests
{
    [Fact]
    public void Merge_locks_stock_archives_and_allows_deleting_every_other_mpq()
    {
        var merged = ClientBrowseMerger.Merge(
            [
                new VolumeDirectoryEntry { Name = "common.MPQ", RelativePath = "Data/common.MPQ", SizeBytes = 10 },
                new VolumeDirectoryEntry { Name = "Interface.MPQ", RelativePath = "Data/Interface.MPQ", SizeBytes = 2 },
            ],
            [
                new VolumeDirectoryEntry { Name = "patch-D.MPQ", RelativePath = "Data/patch-D.MPQ", SizeBytes = 99 },
                new VolumeDirectoryEntry { Name = "Sound.MPQ", RelativePath = "Data/Sound.MPQ", SizeBytes = 3 },
                new VolumeDirectoryEntry { Name = "World.MPQ", RelativePath = "Data/World.MPQ", SizeBytes = 4 },
            ]);

        merged.Single(e => e.Name == "common.MPQ").IsLocked.Should().BeTrue();
        merged.Single(e => e.Name == "Interface.MPQ").IsLocked.Should().BeFalse();
        merged.Single(e => e.Name == "Sound.MPQ").IsLocked.Should().BeFalse();
        merged.Single(e => e.Name == "World.MPQ").IsLocked.Should().BeFalse();
        merged.Single(e => e.Name == "patch-D.MPQ").IsLocked.Should().BeFalse();
        merged.Single(e => e.Name == "patch-D.MPQ").Size.Should().Be(99);
    }
}

public sealed class ClientBaseStripTests
{
    [Fact]
    public void StripPreservedPlatformContent_removes_letter_patches_and_keeps_stock()
    {
        var root = Path.Combine(Path.GetTempPath(), "azp-client-strip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        Directory.CreateDirectory(Path.Combine(root, "Interface", "AddOns", "Foo"));
        File.WriteAllText(Path.Combine(root, "Wow.exe"), "exe");
        File.WriteAllText(Path.Combine(root, "Data", "common.MPQ"), "stock");
        File.WriteAllText(Path.Combine(root, "Data", "patch-D.MPQ"), "custom");
        File.WriteAllText(Path.Combine(root, "Interface", "AddOns", "Foo", "Foo.toc"), "toc");
        try
        {
            ClientService.StripPreservedPlatformContent(root).Should().Be(2);
            File.Exists(Path.Combine(root, "Data", "common.MPQ")).Should().BeTrue();
            File.Exists(Path.Combine(root, "Data", "patch-D.MPQ")).Should().BeFalse();
            File.Exists(Path.Combine(root, "Interface", "AddOns", "Foo", "Foo.toc")).Should().BeFalse();
            File.Exists(Path.Combine(root, "Wow.exe")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

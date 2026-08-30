using AzerothPlatform.Infrastructure.Services.Shared;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Shared;

public sealed class TempWorkspaceTests
{
    [Fact]
    public void A_directory_lives_under_the_sweepable_root_and_goes_on_dispose()
    {
        string path;
        using (var scratch = TempWorkspace.CreateDirectory("unit-dir"))
        {
            path = scratch.Path;
            Directory.Exists(path).Should().BeTrue();
            Path.GetDirectoryName(path).Should().Be(TempWorkspace.Root.TrimEnd(Path.DirectorySeparatorChar));
        }

        Directory.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Contents_go_with_the_directory()
    {
        string path;
        using (var scratch = TempWorkspace.CreateDirectory("unit-tree"))
        {
            path = scratch.Path;
            Directory.CreateDirectory(scratch.Combine("nested", "deeper"));
            File.WriteAllText(scratch.Combine("nested", "deeper", "file.txt"), "x");
        }

        Directory.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void A_file_exists_on_return_so_callers_can_open_it()
    {
        string path;
        using (var scratch = TempWorkspace.CreateFile("unit-file", ".zip"))
        {
            path = scratch.Path;
            File.Exists(path).Should().BeTrue();
            path.Should().EndWith(".zip");
        }

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        var scratch = TempWorkspace.CreateDirectory("unit-twice");

        scratch.Dispose();
        var act = scratch.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public void Two_workspaces_with_the_same_prefix_do_not_collide()
    {
        using var first = TempWorkspace.CreateDirectory("unit-same");
        using var second = TempWorkspace.CreateDirectory("unit-same");

        second.Path.Should().NotBe(first.Path);
    }
}

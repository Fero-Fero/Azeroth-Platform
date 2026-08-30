using AzerothPlatform.Infrastructure.Services.Shared;
using AzerothPlatform.Tests.TestSupport;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Shared;

public sealed class TempWorkspaceSweeperTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _root = new("azp-sweep-root");
    private readonly TempDir _legacy = new("azp-sweep-legacy");

    public void Dispose()
    {
        _root.Dispose();
        _legacy.Dispose();
    }

    [Fact]
    public void Orphans_older_than_the_cutoff_are_collected()
    {
        var stale = Aged(_root.Combine("stale"), Now.AddHours(-7));

        TempWorkspaceSweeper.Sweep(Now, _root.Path, _legacy.Path).Should().Be(1);

        Directory.Exists(stale).Should().BeFalse();
    }

    [Fact]
    public void Work_in_flight_is_left_alone()
    {
        var fresh = Aged(_root.Combine("fresh"), Now.AddMinutes(-30));

        TempWorkspaceSweeper.Sweep(Now, _root.Path, _legacy.Path).Should().Be(0);

        Directory.Exists(fresh).Should().BeTrue();
    }

    [Fact]
    public void A_deep_write_counts_as_activity_even_though_the_parent_looks_untouched()
    {
        var outer = Directory.CreateDirectory(_root.Combine("outer"));
        var inner = Directory.CreateDirectory(Path.Combine(outer.FullName, "inner"));
        inner.LastWriteTimeUtc = Now.AddMinutes(-5).UtcDateTime;
        outer.LastWriteTimeUtc = Now.AddDays(-3).UtcDateTime;
        outer.CreationTimeUtc = Now.AddDays(-3).UtcDateTime;

        TempWorkspaceSweeper.Sweep(Now, _root.Path, _legacy.Path).Should().Be(0);

        Directory.Exists(outer.FullName).Should().BeTrue();
    }

    [Fact]
    public void Stale_files_go_the_same_way_as_directories()
    {
        var path = _root.Combine("stale.archive");
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, Now.AddDays(-1).UtcDateTime);
        File.SetCreationTimeUtc(path, Now.AddDays(-1).UtcDateTime);

        TempWorkspaceSweeper.Sweep(Now, _root.Path, _legacy.Path).Should().Be(1);

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void The_legacy_pass_takes_only_the_prefixes_the_manager_wrote()
    {
        var ours = Aged(_legacy.Combine("azp-patch-import-abc"), Now.AddDays(-2));
        var theirs = Aged(_legacy.Combine("someone-elses-work"), Now.AddDays(-2));

        TempWorkspaceSweeper.Sweep(Now, _root.Path, _legacy.Path).Should().Be(1);

        Directory.Exists(ours).Should().BeFalse();
        Directory.Exists(theirs).Should().BeTrue();
    }

    /// <summary>
    /// The managed root sits inside the OS temp directory that the legacy pass also walks, so it turns
    /// up there as an ordinary entry. Collecting it would take live scratch down with it.
    /// </summary>
    [Fact]
    public void The_managed_root_is_never_a_candidate_of_the_legacy_pass()
    {
        var root = Directory.CreateDirectory(_legacy.Combine("azp-managed-root"));
        root.LastWriteTimeUtc = Now.AddDays(-9).UtcDateTime;
        root.CreationTimeUtc = Now.AddDays(-9).UtcDateTime;

        TempWorkspaceSweeper.Sweep(Now, root.FullName, _legacy.Path).Should().Be(0);

        Directory.Exists(root.FullName).Should().BeTrue();
    }

    private static string Aged(string path, DateTimeOffset touched)
    {
        var directory = Directory.CreateDirectory(path);
        directory.LastWriteTimeUtc = touched.UtcDateTime;
        directory.CreationTimeUtc = touched.UtcDateTime;
        return directory.FullName;
    }
}

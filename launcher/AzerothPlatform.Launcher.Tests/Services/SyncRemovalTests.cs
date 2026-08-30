using AzerothPlatform.Launcher.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Launcher.Tests.Services;

/// <summary>
/// Covers the decision that lets the launcher delete files from a player's install. Getting this wrong
/// either leaves deleted content on disk forever or wipes a working install, so the plausibility guard
/// is tested as carefully as the happy path.
/// </summary>
public sealed class SyncRemovalTests
{
    private static SyncPlan PlanWith(IEnumerable<string> basePaths, IEnumerable<string> managedPaths)
    {
        var plan = new SyncPlan();
        plan.BasePaths.AddRange(basePaths);
        plan.ManagedPaths.AddRange(managedPaths);
        return plan;
    }

    private static SyncPlan PlanWithBase(params string[] basePaths) => PlanWith(basePaths, []);

    [Fact]
    public void Nothing_is_removed_on_a_first_sync()
    {
        var plan = PlanWithBase("Wow.exe", "Data/common.MPQ");

        SyncService.PlanRemovals(plan, PreviouslySyncedPaths.None).Paths.Should().BeEmpty();
    }

    [Fact]
    public void Files_still_in_the_manifest_are_kept()
    {
        var plan = PlanWithBase("Wow.exe", "Data/common.MPQ");
        var previous = new PreviouslySyncedPaths([], ["Wow.exe", "Data/common.MPQ"]);

        SyncService.PlanRemovals(plan, previous).Paths.Should().BeEmpty();
    }

    [Fact]
    public void A_base_file_dropped_from_the_manifest_is_removed()
    {
        // The case base pruning exists for: junk shipped by a bad upload, deleted server-side afterwards.
        var plan = PlanWithBase("Wow.exe");
        var previous = new PreviouslySyncedPaths([], ["Wow.exe", "Data/junk.txt"]);

        SyncService.PlanRemovals(plan, previous).Paths.Should().Equal("Data/junk.txt");
    }

    [Fact]
    public void A_managed_file_dropped_from_the_manifest_is_removed()
    {
        var plan = PlanWith(["Wow.exe"], ["Data/patch-K.MPQ"]);
        var previous = new PreviouslySyncedPaths(["Data/patch-K.MPQ", "Data/patch-J.MPQ"], ["Wow.exe"]);

        SyncService.PlanRemovals(plan, previous).Paths.Should().Equal("Data/patch-J.MPQ");
    }

    [Fact]
    public void A_file_that_changed_group_is_not_removed()
    {
        // Publishing a patch that the uploaded client also carried flips its group. The path is still
        // served, so deleting it would strand the player without the file until the next check.
        var plan = PlanWith([], ["Data/patch-K.MPQ"]);
        var previous = new PreviouslySyncedPaths([], ["Data/patch-K.MPQ"]);

        SyncService.PlanRemovals(plan, previous).Paths.Should().BeEmpty();
    }

    [Fact]
    public void Separators_and_case_do_not_produce_phantom_removals()
    {
        var plan = PlanWithBase("Data/common.MPQ");
        var previous = new PreviouslySyncedPaths([], [@"data\Common.mpq"]);

        SyncService.PlanRemovals(plan, previous).Paths.Should().BeEmpty();
    }

    [Fact]
    public void A_path_recorded_twice_is_removed_once()
    {
        var plan = PlanWithBase("Wow.exe");
        var previous = new PreviouslySyncedPaths(["Data/junk.txt"], ["Wow.exe", @"Data\junk.txt"]);

        SyncService.PlanRemovals(plan, previous).Paths.Should().Equal("Data/junk.txt");
    }

    [Fact]
    public void Blank_entries_are_ignored()
    {
        var plan = PlanWithBase("Wow.exe");
        var previous = new PreviouslySyncedPaths([], ["Wow.exe", "", "   ", "/"]);

        SyncService.PlanRemovals(plan, previous).Paths.Should().BeEmpty();
    }

    [Fact]
    public void A_small_removal_set_is_allowed_even_when_it_is_most_of_the_install()
    {
        // Below the absolute floor the fraction is not consulted: shrinking a tiny install is normal.
        var plan = PlanWithBase("Wow.exe");
        var previous = new PreviouslySyncedPaths(
            [],
            ["Wow.exe", .. Enumerable.Range(0, 40).Select(i => $"Data/junk-{i}.txt")]);

        SyncService.PlanRemovals(plan, previous).Paths.Should().HaveCount(40);
    }

    [Fact]
    public void A_removal_set_that_would_gut_the_install_is_refused()
    {
        var surviving = Enumerable.Range(0, 100).Select(i => $"Data/keep-{i}.MPQ").ToArray();
        var plan = PlanWithBase(surviving);
        var previous = new PreviouslySyncedPaths(
            [],
            [.. surviving, .. Enumerable.Range(0, 51).Select(i => $"Data/gone-{i}.MPQ")]);

        var removals = SyncService.PlanRemovals(plan, previous);

        removals.Paths.Should().BeEmpty();
        removals.RefusedCount.Should().Be(51);
    }

    [Fact]
    public void A_large_but_proportionate_removal_set_is_allowed()
    {
        var surviving = Enumerable.Range(0, 200).Select(i => $"Data/keep-{i}.MPQ").ToArray();
        var plan = PlanWithBase(surviving);
        var previous = new PreviouslySyncedPaths(
            [],
            [.. surviving, .. Enumerable.Range(0, 60).Select(i => $"Data/gone-{i}.MPQ")]);

        var removals = SyncService.PlanRemovals(plan, previous);

        removals.Paths.Should().HaveCount(60);
        removals.RefusedCount.Should().Be(0);
    }

    [Fact]
    public void An_empty_manifest_cannot_authorise_a_large_wipe()
    {
        // An operator purging the client volumes makes the server serve an empty manifest. Every file a
        // player has then looks dropped, and acting on that would delete their whole install.
        var plan = PlanWithBase();
        var previous = new PreviouslySyncedPaths(
            [],
            [.. Enumerable.Range(0, 500).Select(i => $"Data/file-{i}.MPQ")]);

        var removals = SyncService.PlanRemovals(plan, previous);

        removals.Paths.Should().BeEmpty();
        removals.RefusedCount.Should().Be(500);
    }

    [Fact]
    public void An_empty_manifest_leaves_a_small_install_alone_too()
    {
        // Below the absolute floor the fraction check is skipped, so an empty manifest has to be caught
        // on its own rather than sailing through as a legitimate shrink.
        var plan = PlanWithBase();
        var previous = new PreviouslySyncedPaths([], ["Wow.exe", "Data/common.MPQ"]);

        var removals = SyncService.PlanRemovals(plan, previous);

        removals.Paths.Should().BeEmpty();
        removals.RefusedCount.Should().Be(2);
    }
}

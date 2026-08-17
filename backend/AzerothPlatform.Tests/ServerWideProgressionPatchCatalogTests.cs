using AzerothPlatform.Infrastructure.Services.ServerWideProgression;
using AzerothPlatform.Infrastructure.Services.Migrations;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class ServerWideProgressionPatchCatalogTests
{
    [Fact]
    public void Catalog_contains_expected_progression_patch_count()
    {
        ServerWideProgressionPatchCatalog.All.Should().HaveCount(ServerWideProgressionPatchCatalog.ExpectedPatchCount);
        ServerWideProgressionPatchCatalog.ExpectedPatchCount.Should().Be(18);
        ServerWideProgressionPatchCatalog.FindByState(0)!.Slug.Should().Be("START");
        ServerWideProgressionPatchCatalog.FindByState(0)!.IncrementsProgression.Should().BeFalse();
        ServerWideProgressionPatchCatalog.FindByState(11).Should().BeNull();
    }

    [Fact]
    public void FindByIndex_resolves_1_0_as_START()
    {
        var start = ServerWideProgressionPatchCatalog.FindByIndex("1.0");
        start.Should().NotBeNull();
        start!.State.Should().Be(0);
        start.IncrementsProgression.Should().BeFalse();
    }

    [Fact]
    public void PatchIndex_parses_1_0_as_two_component_index()
    {
        PatchIndex.TryParse("1.0", out var index, explicitSub1: true).Should().BeTrue();
        index.ComponentCount.Should().Be(2);
        index.ToIndexString().Should().Be("1.0");
        index.ToEncodedLevel().Should().Be(1_000_000);
    }

    [Fact]
    public void PatchIndex_compute_next_expansion_uses_entry_point_format()
    {
        var next = PatchIndex.ComputeNext(PatchTier.Expansion, 1, Array.Empty<PatchIndex>());
        next.ToIndexString().Should().Be("1.0");
        next.ComponentCount.Should().Be(2);
    }

    [Fact]
    public void PatchIndex_compute_next_expansion_rejects_legacy_root_when_present()
    {
        var existing = new[] { new PatchIndex(1) };
        var act = () => PatchIndex.ComputeNext(PatchTier.Expansion, 1, existing);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void PatchIndex_parses_bare_1_as_expansion_root()
    {
        PatchIndex.TryParse("1", out var index).Should().BeTrue();
        index.ComponentCount.Should().Be(1);
        index.ToIndexString().Should().Be("1");
    }
}

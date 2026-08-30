using AzerothPlatform.ClientContent;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Client;

/// <summary>
/// The launcher carries its own copy of this rule in
/// <c>launcher/AzerothPlatform.Launcher/Services/SharedClientDataFiles.cs</c>, and the two must agree:
/// the manager decides which archives never move to the overlay, the launcher decides which never get
/// stashed under a profile folder. A one-sided edit strands a stock MPQ in the wrong layer and breaks
/// profile switching for everyone.
///
/// Both projects check the same literal list against the same probes, so changing one side alone fails
/// this suite or its launcher twin (<c>SharedClientDataFilesDriftTests</c>).
/// </summary>
public sealed class SharedClientDataFilesDriftTests
{
    internal static readonly string[] Canonical =
    [
        "common.mpq",
        "common-2.mpq",
        "expansion.mpq",
        "lichking.mpq",
        "patch.mpq",
        "patch-2.mpq",
        "patch-3.mpq",
    ];

    /// <summary>Names a future change would plausibly add, so adding one silently is not possible.</summary>
    internal static IEnumerable<string> Probes()
    {
        foreach (var name in Canonical)
        {
            yield return name;
        }

        for (var digit = '4'; digit <= '9'; digit++)
        {
            yield return $"patch-{digit}.mpq";
        }

        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            yield return $"patch-{letter}.mpq";
        }

        yield return "common-3.mpq";
        yield return "expansion-2.mpq";
        yield return "lichking-2.mpq";
        yield return "custom.mpq";
    }

    [Fact]
    public void The_shared_archive_list_matches_the_launchers_copy()
    {
        SharedClientDataFiles.SharedBaseDataMpqFileNames
            .Should().BeEquivalentTo(Canonical);
    }

    [Fact]
    public void Every_probe_is_classified_the_same_way_on_both_sides()
    {
        foreach (var name in Probes())
        {
            SharedClientDataFiles.IsSharedBaseDataFile($"Data/{name}")
                .Should().Be(Canonical.Contains(name, StringComparer.OrdinalIgnoreCase), name);
        }
    }

    [Fact]
    public void A_shared_archive_in_a_subfolder_is_not_the_stock_one()
    {
        SharedClientDataFiles.IsSharedBaseDataFile("Data/enUS/patch.mpq").Should().BeFalse();
        SharedClientDataFiles.IsSharedBaseDataFile("Data/myprofile/patch-2.mpq").Should().BeFalse();
    }

    [Fact]
    public void Overlay_may_not_replace_a_stock_archive()
    {
        SharedClientDataFiles.MustNotServeFromOverlay("Data/patch-3.MPQ").Should().BeTrue();
        SharedClientDataFiles.MustNotServeFromOverlay("Data/patch-W.MPQ").Should().BeFalse();
    }
}

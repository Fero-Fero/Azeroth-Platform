using System.Text.Json;
using AzerothPlatform.Launcher.Models;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Launcher.Tests.Models;

/// <summary>
/// The manager declares its own <c>ClientManifest</c> in
/// <c>backend/AzerothPlatform.Core/Contracts/Client/ClientManifest.cs</c> and serves what this side
/// reads. The two are separate solutions, so nothing but this pair of tests stops a renamed property
/// from shipping and silently reading back as its default in every deployed launcher.
///
/// The enum values are pinned too: they cross the wire as numbers, so swapping them would reclassify
/// every file rather than fail loudly.
/// </summary>
public sealed class ClientManifestContractDriftTests
{
    private static readonly JsonSerializerOptions CamelCase =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void The_manifest_reads_exactly_the_properties_the_manager_serves()
    {
        PropertyNames(new ClientManifest()).Should().BeEquivalentTo(
            ["version", "verifyToken", "generatedAt", "totalSize", "files", "signature"]);
    }

    [Fact]
    public void A_file_entry_reads_exactly_the_properties_the_manager_serves()
    {
        PropertyNames(new ManifestFile()).Should().BeEquivalentTo(
            ["relativePath", "size", "sha256", "group"]);
    }

    [Fact]
    public void File_group_numbering_is_fixed()
    {
        ((int)ManifestFileGroup.Base).Should().Be(0);
        ((int)ManifestFileGroup.Managed).Should().Be(1);
        Enum.GetValues<ManifestFileGroup>().Should().HaveCount(2);
    }

    private static IEnumerable<string> PropertyNames<T>(T value) =>
        JsonSerializer.SerializeToElement(value, CamelCase)
            .EnumerateObject()
            .Select(p => p.Name);
}

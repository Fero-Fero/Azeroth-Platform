using AzerothPlatform.ClientContent;
using AzerothPlatform.Core.Contracts;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class SharedClientDataFilesTests
{
    [Theory]
    [InlineData("Data/patch-2.mpq", ManifestFileGroup.Base)]
    [InlineData("Data/patch-3.mpq", ManifestFileGroup.Base)]
    [InlineData("Data/patch.mpq", ManifestFileGroup.Base)]
    [InlineData("Data/patch-D.MPQ", ManifestFileGroup.Managed)]
    [InlineData("Data/patch-L.MPQ", ManifestFileGroup.Managed)]
    [InlineData("Data/Patch-A.MPQ", ManifestFileGroup.Managed)]
    public void ResolveGroup_treats_shared_blizzard_mpqs_as_base(string path, ManifestFileGroup expected)
    {
        ClientManifestBuilder.ResolveGroup(ClientManifestBuilder.DefaultManagedPrefixes, path)
            .Should().Be(expected);
    }

    [Fact]
    public void IsSharedBaseDataFile_rejects_nested_paths()
    {
        SharedClientDataFiles.IsSharedBaseDataFile("Data/nested/patch-2.mpq").Should().BeFalse();
    }
}

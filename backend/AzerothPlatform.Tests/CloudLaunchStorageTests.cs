using AzerothPlatform.Core.Contracts;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class CloudLaunchStorageTests
{
    [Fact]
    public void ClampDiskSizeGb_UsesOsDefaultWhenOmitted()
    {
        Assert.Equal(40, CloudLaunchStorage.ClampDiskSizeGb(null, windows: false));
        Assert.Equal(80, CloudLaunchStorage.ClampDiskSizeGb(null, windows: true));
    }

    [Theory]
    [InlineData(8, false, 20)]
    [InlineData(8, true, 50)]
    [InlineData(4000, false, 1000)]
    [InlineData(120, false, 120)]
    public void ClampDiskSizeGb_EnforcesBounds(int requested, bool windows, int expected)
    {
        Assert.Equal(expected, CloudLaunchStorage.ClampDiskSizeGb(requested, windows));
    }
}

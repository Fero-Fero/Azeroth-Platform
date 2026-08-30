using AzerothPlatform.Infrastructure.Services.Cloud;
using Xunit;

namespace AzerothPlatform.Tests.Cloud;

public sealed class AwsLaunchInstanceTypeCatalogTests
{
    [Fact]
    public void SelectAvailable_KeepsOnlyFreeTierTypesOfferedInTheLocation()
    {
        var offered = new[] { "t3.micro", "t3.medium", "t3.large", "m5.large", "c5.xlarge" };
        var freeTier = new[]
        {
            new AwsLaunchInstanceTypeCatalog.LaunchType("t3.micro", "x86_64", 2, 1024),
            new AwsLaunchInstanceTypeCatalog.LaunchType("t2.micro", "x86_64", 1, 1024),
        };

        var selected = AwsLaunchInstanceTypeCatalog.SelectAvailable(offered, freeTier);

        Assert.Equal(["t3.micro"], selected.Select(type => type.Type));
    }

    [Fact]
    public void SelectAvailable_FallsBackToKnownFreeTierWhenApiReturnsNone()
    {
        var offered = new[] { "t3.micro", "t3.medium", "t4g.micro", "m5.large" };

        var selected = AwsLaunchInstanceTypeCatalog.SelectAvailable(offered, []);

        Assert.Equal(["t3.micro", "t4g.micro"], selected.Select(type => type.Type));
    }

    [Fact]
    public void FormatLabel_IncludesFreeTierAndResources()
    {
        var type = new AwsLaunchInstanceTypeCatalog.LaunchType("t3.micro", "x86_64", 2, 1024);

        Assert.Equal("t3.micro - Free Tier · 2 vCPU · 1 GiB", AwsLaunchInstanceTypeCatalog.FormatLabel(type));
    }
}

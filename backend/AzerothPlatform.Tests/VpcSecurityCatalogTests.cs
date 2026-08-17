using AzerothPlatform.Core.Contracts;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class VpcSecurityCatalogTests
{
    [Fact]
    public void ProbeIngressSourceSatisfied_UnpinnedAdminSsh_AcceptsRestrictedCidr()
    {
        Assert.True(VpcSecurityCatalog.ProbeIngressSourceSatisfied(
            "0.0.0.0/0",
            "203.0.113.10/32",
            adminSshUnpinned: true));
    }

    [Fact]
    public void ProbeIngressSourceSatisfied_PlayerPort_RequiresPublicOrExactCidr()
    {
        Assert.False(VpcSecurityCatalog.ProbeIngressSourceSatisfied(
            "0.0.0.0/0",
            "203.0.113.10/32",
            adminSshUnpinned: false));
        Assert.True(VpcSecurityCatalog.ProbeIngressSourceSatisfied(
            "0.0.0.0/0",
            "0.0.0.0/0",
            adminSshUnpinned: false));
    }

    [Fact]
    public void ProbeIngressSourceSatisfied_PinnedAdminSsh_RequiresMatchingCidr()
    {
        Assert.True(VpcSecurityCatalog.ProbeIngressSourceSatisfied(
            "203.0.113.10/32",
            "203.0.113.10/32",
            adminSshUnpinned: false));
        Assert.True(VpcSecurityCatalog.ProbeIngressSourceSatisfied(
            "203.0.113.10/32",
            "0.0.0.0/0",
            adminSshUnpinned: false));
        Assert.False(VpcSecurityCatalog.ProbeIngressSourceSatisfied(
            "203.0.113.10/32",
            "198.51.100.20/32",
            adminSshUnpinned: false));
    }

    [Fact]
    public void IsUnpinnedAdminSsh_WhenLaunchCidrWasNotPassedToProbe()
    {
        var ssh = VpcSecurityCatalog.BuildLaunchCloudIngressRules(adminSourceCidr: null)
            .Single(rule => rule.Port == 22);

        Assert.True(VpcSecurityCatalog.IsUnpinnedAdminSsh(ssh));
        Assert.Equal("0.0.0.0/0", ssh.Source);
    }

    [Fact]
    public void IsUnpinnedAdminSsh_FalseWhenAdminCidrIsPinned()
    {
        var ssh = VpcSecurityCatalog.BuildLaunchCloudIngressRules("203.0.113.10/32")
            .Single(rule => rule.Port == 22);

        Assert.False(VpcSecurityCatalog.IsUnpinnedAdminSsh(ssh));
        Assert.Equal("203.0.113.10/32", ssh.Source);
    }
}

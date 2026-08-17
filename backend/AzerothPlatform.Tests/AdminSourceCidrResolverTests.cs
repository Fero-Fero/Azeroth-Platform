using System.Net;
using AzerothPlatform.Core;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class AdminSourceCidrResolverTests
{
    [Fact]
    public void FromForwardedAndRemote_PrefersPublicForwardedIp()
    {
        var cidr = AdminSourceCidrResolver.FromForwardedAndRemote(
            "10.0.0.1, 203.0.113.10",
            IPAddress.Parse("192.168.1.20"));

        Assert.Equal("203.0.113.10/32", cidr);
    }

    [Fact]
    public void FromForwardedAndRemote_UsesRemoteWhenForwardedIsPrivate()
    {
        var cidr = AdminSourceCidrResolver.FromForwardedAndRemote(
            "192.168.0.5",
            IPAddress.Parse("198.51.100.7"));

        Assert.Equal("198.51.100.7/32", cidr);
    }

    [Fact]
    public void FromForwardedAndRemote_ReturnsNullForLoopback()
    {
        var cidr = AdminSourceCidrResolver.FromForwardedAndRemote(null, IPAddress.Loopback);
        Assert.Null(cidr);
    }

    [Fact]
    public void IsUsableAdminSourceIp_RejectsRfc1918()
    {
        Assert.False(AdminSourceCidrResolver.IsUsableAdminSourceIp(IPAddress.Parse("10.1.2.3")));
        Assert.False(AdminSourceCidrResolver.IsUsableAdminSourceIp(IPAddress.Parse("172.17.0.1")));
        Assert.False(AdminSourceCidrResolver.IsUsableAdminSourceIp(IPAddress.Parse("192.168.1.1")));
        Assert.True(AdminSourceCidrResolver.IsUsableAdminSourceIp(IPAddress.Parse("203.0.113.10")));
    }
}

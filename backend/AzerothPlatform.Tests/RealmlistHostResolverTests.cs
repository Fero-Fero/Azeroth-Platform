using AzerothPlatform.Infrastructure.Services;
using Xunit;

namespace AzerothPlatform.Tests;

public class RealmlistHostResolverTests
{
    [Theory]
    [InlineData("logon.example.com", "logon.example.com")]
    [InlineData("http://logon.example.com", "logon.example.com")]
    [InlineData("https://logon.example.com/", "logon.example.com")]
    [InlineData("http://logon.example.com:3724", "logon.example.com")]
    [InlineData("203.0.113.10:3724", "203.0.113.10")]
    [InlineData("203.0.113.10", "203.0.113.10")]
    public void NormalizeHost_strips_scheme_and_port(string input, string expected)
    {
        Assert.Equal(expected, RealmlistHostResolver.NormalizeHost(input));
    }
}

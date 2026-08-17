using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services.RemoteHost;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class RemoteHostOsProbeTests
{
    [Theory]
    [InlineData("Linux", null, RemoteHostOs.Linux)]
    [InlineData("linux\n", null, RemoteHostOs.Linux)]
    [InlineData("Darwin", null, RemoteHostOs.Linux)]
    [InlineData("MINGW64_NT-10.0", null, RemoteHostOs.Windows)]
    [InlineData(null, "Windows_NT", RemoteHostOs.Windows)]
    [InlineData("", "Windows_NT\r\n", RemoteHostOs.Windows)]
    public void Interpret_RecognizesKnownHosts(string? uname, string? windowsOs, RemoteHostOs expected)
    {
        Assert.Equal(expected, RemoteHostOsProbe.Interpret(uname, windowsOs));
    }

    [Fact]
    public void Interpret_ReturnsNullWhenUnknown()
    {
        Assert.Null(RemoteHostOsProbe.Interpret(null, null));
        Assert.Null(RemoteHostOsProbe.Interpret("busybox", ""));
    }
}

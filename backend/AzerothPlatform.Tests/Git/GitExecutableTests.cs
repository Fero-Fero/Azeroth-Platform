using System.Diagnostics;
using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests.Git;

public sealed class GitExecutableTests
{
    [Fact]
    public void ApplyTo_forces_protocol_v1_and_http11()
    {
        var startInfo = new ProcessStartInfo();
        GitExecutable.ApplyTo(startInfo);

        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.CreateNoWindow.Should().BeTrue();
        startInfo.Environment["GIT_TERMINAL_PROMPT"].Should().Be("0");
        startInfo.ArgumentList.Should().Equal("-c", "protocol.version=1", "-c", "http.version=HTTP/1.1");
    }

    [Fact]
    public void ApplyTo_prefixes_string_Arguments_without_mixing_ArgumentList()
    {
        var startInfo = new ProcessStartInfo
        {
            Arguments = "rev-parse --abbrev-ref HEAD"
        };

        GitExecutable.ApplyTo(startInfo);

        startInfo.Arguments.Should().Be("-c protocol.version=1 -c http.version=HTTP/1.1 rev-parse --abbrev-ref HEAD");
        startInfo.ArgumentList.Should().BeEmpty();
    }
}

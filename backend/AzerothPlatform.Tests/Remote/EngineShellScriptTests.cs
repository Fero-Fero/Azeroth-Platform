using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services;
using Xunit;

namespace AzerothPlatform.Tests.Remote;

/// <summary>
/// Scripts embedded in C# raw string literals pick up whatever line endings the source file was
/// checked out with, and a POSIX shell reads a trailing CR as part of the last word on the line. These
/// tests fail on a Windows checkout if a script stops normalising its line endings.
/// </summary>
public class EngineShellScriptTests
{
    [Fact]
    public void ClientArchiveInstallScriptUsesUnixLineEndings()
    {
        var script = RemoteEngineService.BuildClientArchiveInstallScript("upload.archive");

        Assert.DoesNotContain('\r', script);
        Assert.StartsWith("set -e\n", script);
    }

    [Fact]
    public void VpcBootstrapLaunchScriptUsesUnixLineEndings()
    {
        var script = VpcBootstrapUserData.BuildLaunchScript();

        Assert.DoesNotContain('\r', script);
        Assert.StartsWith("#!/bin/bash\n", script);
    }
}

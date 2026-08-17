using AzerothPlatform.Infrastructure.Services;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class DockerComposeOverrideGeneratorTests
{
    [Fact]
    public void GameServices_EnableAutoSetupAndSkipCreatePrompt()
    {
        var yaml = DockerComposeOverrideGenerator.Generate("abc123", "test", serviceEnvironment: null);

        Assert.Contains("ac-db-import:", yaml, StringComparison.Ordinal);
        Assert.Contains("AC_UPDATES_ENABLE_DATABASES: \"7\"", yaml, StringComparison.Ordinal);
        Assert.Contains("AC_UPDATES_AUTO_SETUP: \"1\"", yaml, StringComparison.Ordinal);
        Assert.Contains("AC_DISABLE_INTERACTIVE: \"1\"", yaml, StringComparison.Ordinal);
        Assert.Contains("ac-authserver:", yaml, StringComparison.Ordinal);
        Assert.Contains("ac-worldserver:", yaml, StringComparison.Ordinal);

        var importIdx = yaml.IndexOf("ac-db-import:", StringComparison.Ordinal);
        var authIdx = yaml.IndexOf("ac-authserver:", StringComparison.Ordinal);
        var worldIdx = yaml.IndexOf("ac-worldserver:", StringComparison.Ordinal);
        Assert.True(yaml.IndexOf("AC_DISABLE_INTERACTIVE: \"1\"", importIdx, StringComparison.Ordinal) > importIdx);
        Assert.True(yaml.IndexOf("AC_DISABLE_INTERACTIVE: \"1\"", authIdx, StringComparison.Ordinal) > authIdx);
        Assert.True(yaml.IndexOf("AC_DISABLE_INTERACTIVE: \"1\"", worldIdx, StringComparison.Ordinal) > worldIdx);
    }
}

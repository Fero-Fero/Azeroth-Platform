using AzerothPlatform.Infrastructure.Services.ServerWideProgression;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class PatchLauncherConfigTests
{
    [Theory]
    [InlineData("classic")]
    [InlineData("tbc")]
    [InlineData("wotlk")]
    public void TryParseTheme_accepts_valid_themes(string theme)
    {
        PatchLauncherConfig.TryParseTheme($$"""{"theme":"{{theme}}"}""", out var parsed, out var error)
            .Should().BeTrue();
        parsed.Should().Be(theme);
        error.Should().BeNull();
    }

    [Fact]
    public void TryParseTheme_rejects_invalid_theme()
    {
        PatchLauncherConfig.TryParseTheme("""{"theme":"retail"}""", out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("invalid");
    }

    [Fact]
    public void TryParseTheme_rejects_missing_theme_property()
    {
        PatchLauncherConfig.TryParseTheme("{}", out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("theme");
    }
}

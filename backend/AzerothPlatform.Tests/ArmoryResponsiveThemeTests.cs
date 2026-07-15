using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class ArmoryResponsiveThemeTests
{
    [Fact]
    public void BuildCss_expands_armory_container_below_1200px()
    {
        var css = ArmoryResponsiveTheme.BuildCss();

        css.Should().Contain("@media screen and (max-width: 1200px)");
        css.Should().Contain(".armory-container");
        css.Should().Contain("width: 100% !important");
        css.Should().Contain("max-width: none !important");
    }
}

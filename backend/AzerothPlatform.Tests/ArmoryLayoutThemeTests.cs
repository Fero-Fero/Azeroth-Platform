using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class ArmoryLayoutThemeTests
{
    [Fact]
    public void BuildCss_generates_per_page_grid_config()
    {
        var layout = new ArmoryLayoutDto
        {
            Version = 2,
            Pages = new Dictionary<string, ArmoryPageLayoutDto>(StringComparer.OrdinalIgnoreCase)
            {
                ["home"] = new ArmoryPageLayoutDto
                {
                    Widgets =
                    [
                        new ArmoryLayoutWidgetDto
                        {
                            Id = "w-test",
                            Type = ArmoryWidgetType.News,
                            X = 0,
                            Y = 0,
                            W = 6,
                            H = 4,
                            Visible = true,
                        },
                    ],
                },
            },
        };

        var css = ArmoryLayoutTheme.BuildCss(layout);

        css.Should().Contain("[data-armory-page=\"home\"]");
        css.Should().Contain("grid-template-columns: repeat(12, minmax(0, 1fr));");
        css.Should().Contain("gap: 12px;");
        css.Should().Contain("grid-auto-rows: minmax(48px, auto);");
    }
}

using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class ArmoryLayoutDefaultsTests
{
    [Fact]
    public void Normalize_compacts_visible_widgets_vertically_on_home_page()
    {
        var layout = new ArmoryLayoutDto
        {
            Pages = new Dictionary<string, ArmoryPageLayoutDto>(StringComparer.OrdinalIgnoreCase)
            {
                [ArmoryPageIds.Home] = new ArmoryPageLayoutDto
                {
                    Grid = new ArmoryLayoutGridDto { Columns = 12, RowHeight = 48, Gap = 12 },
                    Widgets =
                    [
                        new ArmoryLayoutWidgetDto
                        {
                            Id = "top",
                            Type = ArmoryWidgetType.PageTitle,
                            X = 0,
                            Y = 0,
                            W = 12,
                            H = 1,
                            Visible = true,
                        },
                        new ArmoryLayoutWidgetDto
                        {
                            Id = "bottom",
                            Type = ArmoryWidgetType.News,
                            X = 0,
                            Y = 8,
                            W = 12,
                            H = 4,
                            Visible = true,
                        },
                    ],
                },
            },
        };

        var normalized = ArmoryLayoutDefaults.Normalize(layout);
        var home = normalized.Pages[ArmoryPageIds.Home];

        home.Widgets.Single(w => w.Id == "top").Y.Should().Be(0);
        home.Widgets.Single(w => w.Id == "top").H.Should().Be(1);
        home.Widgets.Single(w => w.Id == "bottom").Y.Should().Be(1);
    }

    [Fact]
    public void Normalize_migrates_v1_root_widgets_to_home_page()
    {
        var layout = new ArmoryLayoutDto
        {
            Version = 1,
            Grid = new ArmoryLayoutGridDto { Columns = 12 },
            Widgets =
            [
                new ArmoryLayoutWidgetDto
                {
                    Id = "only",
                    Type = ArmoryWidgetType.PageTitle,
                    X = 0,
                    Y = 0,
                    W = 12,
                    H = 1,
                    Visible = true,
                },
            ],
        };

        var normalized = ArmoryLayoutDefaults.Normalize(layout);

        normalized.Version.Should().Be(2);
        normalized.Pages[ArmoryPageIds.Home].Widgets.Should().ContainSingle(w => w.Id == "only");
    }

    [Fact]
    public void Normalize_strips_realm_selector_widgets()
    {
        var layout = new ArmoryLayoutDto
        {
            Pages = new Dictionary<string, ArmoryPageLayoutDto>(StringComparer.OrdinalIgnoreCase)
            {
                [ArmoryPageIds.Home] = new ArmoryPageLayoutDto
                {
                    Widgets =
                    [
                        new ArmoryLayoutWidgetDto
                        {
                            Id = "realm",
                            Type = ArmoryWidgetType.RealmSelector,
                            X = 0,
                            Y = 0,
                            W = 12,
                            H = 1,
                            Visible = true,
                        },
                        new ArmoryLayoutWidgetDto
                        {
                            Id = "search",
                            Type = ArmoryWidgetType.CharacterSearch,
                            X = 0,
                            Y = 1,
                            W = 12,
                            H = 4,
                            Visible = true,
                        },
                    ],
                },
            },
        };

        var normalized = ArmoryLayoutDefaults.Normalize(layout);
        normalized.Pages[ArmoryPageIds.Home].Widgets.Should().NotContain(w => w.Type == ArmoryWidgetType.RealmSelector);
    }

    [Fact]
    public void Character_classic_template_places_overview_cards_below_model_with_full_height_stats()
    {
        var page = ArmoryLayoutDefaults.PageTemplate(ArmoryPageIds.Character, "Default");
        var model = page.Widgets.Single(w => w.Type == ArmoryWidgetType.CharacterModelViewer);
        var stats = page.Widgets.Single(w => w.Type == ArmoryWidgetType.CharacterStats);
        var cards = page.Widgets.Single(w => w.Type == ArmoryWidgetType.CharacterOverviewCards);

        model.X.Should().Be(0);
        model.Y.Should().Be(2);
        model.W.Should().Be(8);
        model.H.Should().Be(8);

        stats.X.Should().Be(8);
        stats.Y.Should().Be(2);
        stats.W.Should().Be(4);
        stats.H.Should().Be(11);

        cards.X.Should().Be(0);
        cards.Y.Should().Be(11);
        cards.W.Should().Be(8);
        cards.H.Should().Be(3);
    }
}

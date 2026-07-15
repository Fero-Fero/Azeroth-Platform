using System.Text.Json;
using AzerothPlatform.Core.Contracts;
using AzerothPlatform.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace AzerothPlatform.Tests;

public sealed class ArmoryLayoutSerializationTests
{
    [Fact]
    public void ToRuntimeJson_uses_camelCase_property_names_for_v2_pages()
    {
        var layout = ArmoryLayoutDefaults.Normalize(new ArmoryLayoutDto
        {
            Pages = new Dictionary<string, ArmoryPageLayoutDto>(StringComparer.OrdinalIgnoreCase)
            {
                ["home"] = new ArmoryPageLayoutDto
                {
                    Grid = new ArmoryLayoutGridDto { Columns = 12, RowHeight = 48, Gap = 12 },
                    Widgets =
                    [
                        new ArmoryLayoutWidgetDto
                        {
                            Id = "test-widget",
                            Type = ArmoryWidgetType.News,
                            X = 0,
                            Y = 1,
                            W = 6,
                            H = 4,
                            Visible = true,
                        },
                    ],
                },
            },
            Navbar = new ArmoryNavbarDto
            {
                ShowSearch = true,
                Links =
                [
                    new ArmoryNavbarLinkDto { Id = "nav-home", Kind = ArmoryNavbarLinkKind.Home, Visible = true },
                ],
            },
        });

        var json = ArmoryLayoutSerialization.ToRuntimeJson(layout);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("version").GetInt32().Should().Be(2);
        root.TryGetProperty("pages", out var pages).Should().BeTrue();
        root.TryGetProperty("Pages", out _).Should().BeFalse();

        var home = pages.GetProperty("home");
        home.TryGetProperty("widgets", out var widgets).Should().BeTrue();
        widgets[0].GetProperty("type").GetString().Should().Be("News");
        home.GetProperty("grid").GetProperty("columns").GetInt32().Should().Be(12);

        root.TryGetProperty("navbar", out var navbar).Should().BeTrue();
        navbar.TryGetProperty("links", out _).Should().BeTrue();
    }

    [Fact]
    public void FromRuntimeJson_reads_camelCase_saved_layout_for_image_rebuild()
    {
        var layout = ArmoryLayoutDefaults.Normalize(new ArmoryLayoutDto
        {
            Pages = new Dictionary<string, ArmoryPageLayoutDto>(StringComparer.OrdinalIgnoreCase)
            {
                ["home"] = new ArmoryPageLayoutDto
                {
                    Mode = ArmoryLayoutMode.Custom,
                    TemplateId = "Custom",
                    Grid = new ArmoryLayoutGridDto { Columns = 12, RowHeight = 48, Gap = 12 },
                    Widgets =
                    [
                        new ArmoryLayoutWidgetDto
                        {
                            Id = "custom-news",
                            Type = ArmoryWidgetType.News,
                            X = 0,
                            Y = 4,
                            W = 6,
                            H = 4,
                            Visible = true,
                        },
                    ],
                },
                ["character"] = new ArmoryPageLayoutDto
                {
                    Mode = ArmoryLayoutMode.Template,
                    TemplateId = "AowowDense",
                    Grid = new ArmoryLayoutGridDto { Columns = 12, RowHeight = 48, Gap = 12 },
                    Widgets =
                    [
                        new ArmoryLayoutWidgetDto
                        {
                            Id = "tpl-model",
                            Type = ArmoryWidgetType.CharacterModelViewer,
                            X = 0,
                            Y = 3,
                            W = 12,
                            H = 6,
                            Visible = true,
                        },
                    ],
                },
            },
        });

        var json = ArmoryLayoutSerialization.ToRuntimeJson(layout);
        var strict = JsonSerializer.Deserialize<ArmoryLayoutDto>(json);
        var runtime = ArmoryLayoutSerialization.FromRuntimeJson(json);

        strict!.Pages.Should().BeEmpty();

        runtime.Should().NotBeNull();
        runtime!.Version.Should().Be(2);
        runtime.Pages.Should().ContainKey("home");
        runtime.Pages["home"].Mode.Should().Be(ArmoryLayoutMode.Custom);
        runtime.Pages["home"].TemplateId.Should().Be("Custom");
        runtime.Pages["character"].TemplateId.Should().Be("AowowDense");
        runtime.Pages["character"].Widgets[0].W.Should().Be(12);
    }
}

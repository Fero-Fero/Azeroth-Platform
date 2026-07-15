using System.Text.Json;
using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>Built-in per-page layout templates, V2 normalization, and V1 migration.</summary>
internal static class ArmoryLayoutDefaults
{
    public static ArmoryLayoutDto Default() => BuildSite();

    public static ArmoryPageLayoutDto DefaultPage(string pageId) =>
        PageTemplate(pageId, "Default");

    public static ArmoryPageLayoutDto PageTemplate(string pageId, string templateId) =>
        (pageId.ToLowerInvariant(), templateId) switch
        {
            (ArmoryPageIds.Home, "NewsFocus") => HomeNewsFocus(),
            (ArmoryPageIds.Home, "CharactersFocus") => HomeCharactersFocus(),
            (ArmoryPageIds.Home, "Dashboard") => HomeDashboard(),
            (ArmoryPageIds.Home, "SingleColumn") => HomeDefault(),
            (ArmoryPageIds.Home, _) => HomeDefault(),

            (ArmoryPageIds.Character, "WowheadProfile") => CharacterWowheadProfile(),
            (ArmoryPageIds.Character, "AowowDense") => CharacterAowowDense(),
            (ArmoryPageIds.Character, _) => CharacterClassicStack(),

            (ArmoryPageIds.Connect, "IcyVeinsHero") => ConnectIcyVeinsHero(),
            (ArmoryPageIds.Connect, _) => ConnectDefault(),

            (ArmoryPageIds.NewsList, "IcyVeinsHero") => NewsListIcyVeinsHero(),
            (ArmoryPageIds.NewsList, _) => NewsListDefault(),

            (ArmoryPageIds.Guild, "AowowDense") => GuildAowowDense(),
            (ArmoryPageIds.Guild, _) => GuildDefault(),

            (ArmoryPageIds.TopLogs, "AowowDense") => TopLogsAowowDense(),
            (ArmoryPageIds.TopLogs, _) => TopLogsDefault(),

            (ArmoryPageIds.Map, _) => MapDefault(),

            _ when pageId.StartsWith("character-", StringComparison.Ordinal) => CharacterSubpageDefault(),
            _ => HomeDefault(),
        };

    public static ArmoryLayoutDto Normalize(ArmoryLayoutDto? layout)
    {
        var site = MigrateToV2(layout);
        var pages = new Dictionary<string, ArmoryPageLayoutDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var pageId in ArmoryPageIds.All)
        {
            if (site.Pages.TryGetValue(pageId, out var page) && page.Widgets.Count > 0)
            {
                pages[pageId] = NormalizePage(page, pageId);
            }
            else
            {
                pages[pageId] = NormalizePage(DefaultPage(pageId), pageId);
            }
        }

        return new ArmoryLayoutDto
        {
            Version = 2,
            Navbar = NormalizeNavbar(site.Navbar),
            Pages = pages,
        };
    }

    public static ArmoryPageLayoutDto NormalizePage(ArmoryPageLayoutDto? page, string pageId)
    {
        page ??= DefaultPage(pageId);
        page = MaybeRefreshLegacyCharacterTemplate(page, pageId);
        page = MaybeRefreshLegacyCharacterSubpage(page, pageId);
        page = MaybeRefreshCharacterSubnavHeight(page);
        var columns = page.Grid.Columns is > 0 and <= 24 ? page.Grid.Columns : 12;
        var rowHeight = page.Grid.RowHeight is > 0 and <= 200 ? page.Grid.RowHeight : 48;
        var gap = page.Grid.Gap is >= 0 and <= 64 ? page.Grid.Gap : 12;

        var widgets = new List<ArmoryLayoutWidgetDto>();
        foreach (var widget in page.Widgets)
        {
            if (widget.Type == ArmoryWidgetType.RealmSelector)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(widget.Id))
            {
                widget.Id = Guid.NewGuid().ToString("N");
            }

            widget.X = Math.Max(0, widget.X);
            widget.Y = Math.Max(0, widget.Y);
            widget.W = Math.Clamp(widget.W, MinWidth(widget.Type), columns);
            widget.H = Math.Clamp(widget.H, MinHeight(widget.Type), 24);
            if (widget.X + widget.W > columns)
            {
                widget.X = Math.Max(0, columns - widget.W);
            }

            widgets.Add(widget);
        }

        if (page.Mode != ArmoryLayoutMode.Custom)
        {
            CompactWidgetsVertically(widgets, columns);
        }

        var templateId = string.IsNullOrWhiteSpace(page.TemplateId) ? "Default" : page.TemplateId;
        if (string.Equals(pageId, ArmoryPageIds.Home, StringComparison.OrdinalIgnoreCase)
            && string.Equals(templateId, "SingleColumn", StringComparison.OrdinalIgnoreCase))
        {
            templateId = "Default";
        }

        return new ArmoryPageLayoutDto
        {
            Mode = page.Mode,
            TemplateId = templateId,
            Grid = new ArmoryLayoutGridDto { Columns = columns, RowHeight = rowHeight, Gap = gap },
            Widgets = widgets.OrderBy(w => w.Y).ThenBy(w => w.X).ToList(),
        };
    }

    private static ArmoryPageLayoutDto MaybeRefreshLegacyCharacterTemplate(ArmoryPageLayoutDto page, string pageId)
    {
        if (!string.Equals(pageId, ArmoryPageIds.Character, StringComparison.OrdinalIgnoreCase)
            || page.Mode == ArmoryLayoutMode.Custom)
        {
            return page;
        }

        var model = page.Widgets.FirstOrDefault(w => w.Type == ArmoryWidgetType.CharacterModelViewer);
        if (model is null)
        {
            return page;
        }

        var cards = page.Widgets.FirstOrDefault(w => w.Type == ArmoryWidgetType.CharacterOverviewCards);
        var templateId = string.IsNullOrWhiteSpace(page.TemplateId) ? "Default" : page.TemplateId;
        var needsClassicRefresh = string.Equals(templateId, "Default", StringComparison.OrdinalIgnoreCase)
            && (model.W >= 12 || (model.W <= 8 && cards is not null && cards.W >= 12));
        var needsWowheadRefresh = string.Equals(templateId, "WowheadProfile", StringComparison.OrdinalIgnoreCase) && model.W <= 8;
        if (!needsClassicRefresh && !needsWowheadRefresh)
        {
            return page;
        }

        return PageTemplate(pageId, templateId);
    }

    private static ArmoryPageLayoutDto MaybeRefreshCharacterSubnavHeight(ArmoryPageLayoutDto page)
    {
        var changed = false;
        foreach (var widget in page.Widgets.Where(w => w.Type == ArmoryWidgetType.CharacterSubnav && w.H < 2))
        {
            widget.H = 2;
            changed = true;
        }

        return changed
            ? new ArmoryPageLayoutDto
            {
                TemplateId = page.TemplateId,
                Mode = page.Mode,
                Grid = page.Grid,
                Widgets = page.Widgets.ToList(),
            }
            : page;
    }

    private static ArmoryPageLayoutDto MaybeRefreshLegacyCharacterSubpage(ArmoryPageLayoutDto page, string pageId)
    {
        if (page.Mode == ArmoryLayoutMode.Custom)
        {
            return page;
        }

        var isCharacterPage = string.Equals(pageId, ArmoryPageIds.Character, StringComparison.OrdinalIgnoreCase)
            || pageId.StartsWith("character-", StringComparison.OrdinalIgnoreCase);
        if (!isCharacterPage)
        {
            return page;
        }

        var hasLegacySpacer = page.Widgets.Any(w => w.Type == ArmoryWidgetType.Spacer);
        if (!hasLegacySpacer)
        {
            return page;
        }

        // Character overview/subpage content outside the grid should not reserve spacer rows.
        return new ArmoryPageLayoutDto
        {
            TemplateId = page.TemplateId,
            Mode = page.Mode,
            Grid = page.Grid,
            Widgets = page.Widgets.Where(w => w.Type != ArmoryWidgetType.Spacer).ToList(),
        };
    }

    public static ArmoryLayoutDto MigrateToV2(ArmoryLayoutDto? layout)
    {
        if (layout is null)
        {
            return Default();
        }

        if (layout.Version >= 2 && layout.Pages.Count > 0)
        {
            return layout;
        }

        var home = new ArmoryPageLayoutDto
        {
            Mode = layout.Mode ?? ArmoryLayoutMode.Template,
            TemplateId = layout.TemplateId?.ToString() ?? ArmoryLayoutTemplateId.Default.ToString(),
            Grid = layout.Grid ?? new ArmoryLayoutGridDto(),
            Widgets = layout.Widgets?.ToList() ?? [],
        };

        if (home.Widgets.Count == 0)
        {
            return Default();
        }

        return new ArmoryLayoutDto
        {
            Version = 2,
            Navbar = layout.Navbar,
            Pages = new Dictionary<string, ArmoryPageLayoutDto>(StringComparer.OrdinalIgnoreCase)
            {
                [ArmoryPageIds.Home] = home,
            },
        };
    }

    public static ArmoryLayoutDto Template(ArmoryLayoutTemplateId templateId) =>
        BuildSite(homeTemplateId: templateId.ToString());

    public static ArmoryNavbarDto DefaultNavbar() => new()
    {
        ShowSearch = true,
        SearchPlaceholder = "Search character...",
        Links =
        [
            new ArmoryNavbarLinkDto { Id = "nav-home", Kind = ArmoryNavbarLinkKind.Home, Visible = true },
            new ArmoryNavbarLinkDto { Id = "nav-top-logs", Kind = ArmoryNavbarLinkKind.TopLogs, Visible = true },
            new ArmoryNavbarLinkDto { Id = "nav-map", Kind = ArmoryNavbarLinkKind.Map, Visible = true },
            new ArmoryNavbarLinkDto { Id = "nav-connect", Kind = ArmoryNavbarLinkKind.Connect, Visible = true },
        ]
    };

    public static ArmoryNavbarDto NormalizeNavbar(ArmoryNavbarDto? navbar)
    {
        if (navbar is null || navbar.Links.Count == 0)
        {
            return DefaultNavbar();
        }

        var links = new List<ArmoryNavbarLinkDto>();
        foreach (var link in navbar.Links)
        {
            if (string.IsNullOrWhiteSpace(link.Id))
            {
                link.Id = Guid.NewGuid().ToString("N");
            }

            if (link.Kind == ArmoryNavbarLinkKind.Custom)
            {
                if (string.IsNullOrWhiteSpace(link.Label) || string.IsNullOrWhiteSpace(link.Href))
                {
                    continue;
                }
            }

            links.Add(link);
        }

        if (links.All(l => l.Kind != ArmoryNavbarLinkKind.Home))
        {
            links.Insert(0, new ArmoryNavbarLinkDto { Id = "nav-home", Kind = ArmoryNavbarLinkKind.Home, Visible = true });
        }

        return new ArmoryNavbarDto
        {
            ShowSearch = navbar.ShowSearch,
            SearchPlaceholder = string.IsNullOrWhiteSpace(navbar.SearchPlaceholder)
                ? "Search character..."
                : navbar.SearchPlaceholder.Trim(),
            Links = links
        };
    }

    public static int GetIntSetting(ArmoryLayoutWidgetDto widget, string key, int fallback)
    {
        if (widget.Settings is null || !widget.Settings.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => fallback
        };
    }

    public static string GetStringSetting(ArmoryLayoutWidgetDto widget, string key, string fallback)
    {
        if (widget.Settings is null || !widget.Settings.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    }

    public static bool GetBoolSetting(ArmoryLayoutWidgetDto widget, string key, bool fallback)
    {
        if (widget.Settings is null || !widget.Settings.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    private static ArmoryLayoutDto BuildSite(string homeTemplateId = "Default") =>
        new()
        {
            Version = 2,
            Navbar = DefaultNavbar(),
            Pages = ArmoryPageIds.All.ToDictionary(
                id => id,
                id => id == ArmoryPageIds.Home
                    ? PageTemplate(ArmoryPageIds.Home, homeTemplateId)
                    : DefaultPage(id),
                StringComparer.OrdinalIgnoreCase),
        };

    private static ArmoryPageLayoutDto Page(string templateId, params ArmoryLayoutWidgetDto[] widgets) =>
        new()
        {
            Mode = ArmoryLayoutMode.Template,
            TemplateId = templateId,
            Grid = new ArmoryLayoutGridDto { Columns = 12, RowHeight = 48, Gap = 12 },
            Widgets = widgets.ToList(),
        };

    private static ArmoryPageLayoutDto HomeDefault() => Page("Default",
        Widget("pt", ArmoryWidgetType.PageTitle, 0, 0, 12, 1),
        Widget("news", ArmoryWidgetType.News, 0, 1, 12, 4, newsSettings: 3),
        Widget("recent", ArmoryWidgetType.RecentCharacters, 0, 5, 12, 3),
        Widget("search", ArmoryWidgetType.CharacterSearch, 0, 8, 12, 5));

    private static ArmoryPageLayoutDto HomeNewsFocus() => Page("NewsFocus",
        Widget("pt", ArmoryWidgetType.PageTitle, 0, 0, 12, 1),
        Widget("news", ArmoryWidgetType.News, 0, 1, 12, 5, newsSettings: 3),
        Widget("recent", ArmoryWidgetType.RecentCharacters, 0, 6, 6, 3),
        Widget("search", ArmoryWidgetType.CharacterSearch, 6, 6, 6, 5));

    private static ArmoryPageLayoutDto HomeCharactersFocus() => Page("CharactersFocus",
        Widget("pt", ArmoryWidgetType.PageTitle, 0, 0, 12, 1),
        Widget("recent", ArmoryWidgetType.RecentCharacters, 0, 1, 12, 3),
        Widget("news", ArmoryWidgetType.News, 0, 4, 6, 4, newsSettings: 3),
        Widget("search", ArmoryWidgetType.CharacterSearch, 6, 4, 6, 5));

    private static ArmoryPageLayoutDto HomeDashboard() => Page("Dashboard",
        Widget("pt", ArmoryWidgetType.PageTitle, 0, 0, 12, 1),
        Widget("news", ArmoryWidgetType.News, 0, 1, 6, 4, newsSettings: 3),
        Widget("recent", ArmoryWidgetType.RecentCharacters, 6, 1, 6, 4),
        Widget("search", ArmoryWidgetType.CharacterSearch, 0, 5, 12, 5));

    private static ArmoryPageLayoutDto CharacterClassicStack() => Page("Default",
        Widget("hdr", ArmoryWidgetType.CharacterHeader, 0, 0, 12, 2),
        Widget("model", ArmoryWidgetType.CharacterModelViewer, 0, 2, 8, 8),
        Widget("stats", ArmoryWidgetType.CharacterStats, 8, 2, 4, 11),
        Widget("cards", ArmoryWidgetType.CharacterOverviewCards, 0, 11, 8, 3));

    private static ArmoryPageLayoutDto CharacterWowheadProfile() => Page("WowheadProfile",
        Widget("hdr", ArmoryWidgetType.CharacterHeader, 0, 0, 12, 2),
        Widget("model", ArmoryWidgetType.CharacterModelViewer, 0, 2, 12, 6),
        Widget("stats", ArmoryWidgetType.CharacterStats, 0, 8, 12, 4),
        Widget("cards", ArmoryWidgetType.CharacterOverviewCards, 0, 12, 12, 3));

    private static ArmoryPageLayoutDto CharacterAowowDense() => Page("AowowDense",
        Widget("hdr", ArmoryWidgetType.CharacterHeader, 0, 0, 12, 2),
        Widget("model", ArmoryWidgetType.CharacterModelViewer, 0, 2, 12, 6),
        Widget("stats", ArmoryWidgetType.CharacterStats, 0, 8, 6, 4),
        Widget("cards", ArmoryWidgetType.CharacterOverviewCards, 6, 8, 6, 4));

    private static ArmoryPageLayoutDto CharacterSubpageDefault() => Page("WowheadTabs",
        Widget("subnav", ArmoryWidgetType.CharacterSubnav, 0, 0, 12, 2));

    private static ArmoryPageLayoutDto ConnectDefault() => Page("Default",
        Widget("pt", ArmoryWidgetType.PageTitle, 0, 0, 12, 1),
        Widget("cta", ArmoryWidgetType.ConnectCta, 0, 1, 12, 4));

    private static ArmoryPageLayoutDto ConnectIcyVeinsHero() => Page("IcyVeinsHero",
        Widget("cta", ArmoryWidgetType.ConnectCta, 0, 0, 12, 6));

    private static ArmoryPageLayoutDto NewsListDefault() => Page("Default",
        Widget("pt", ArmoryWidgetType.PageTitle, 0, 0, 12, 1),
        Widget("feed", ArmoryWidgetType.NewsFeed, 0, 1, 12, 8));

    private static ArmoryPageLayoutDto NewsListIcyVeinsHero() => Page("IcyVeinsHero",
        Widget("pt", ArmoryWidgetType.PageTitle, 0, 0, 12, 1),
        Widget("feed", ArmoryWidgetType.NewsFeed, 0, 1, 12, 10));

    private static ArmoryPageLayoutDto GuildDefault() => Page("Default",
        Widget("hdr", ArmoryWidgetType.GuildHeader, 0, 0, 12, 2),
        Widget("roster", ArmoryWidgetType.GuildRoster, 0, 2, 12, 8));

    private static ArmoryPageLayoutDto GuildAowowDense() => GuildDefault();

    private static ArmoryPageLayoutDto TopLogsDefault() => Page("Default",
        Widget("pt", ArmoryWidgetType.PageTitle, 0, 0, 12, 1),
        Widget("logs", ArmoryWidgetType.TopLogsTable, 0, 1, 12, 9));

    private static ArmoryPageLayoutDto TopLogsAowowDense() => TopLogsDefault();

    private static ArmoryPageLayoutDto MapDefault() => Page("Default",
        Widget("map", ArmoryWidgetType.MapCanvas, 0, 0, 12, 12));

    private static ArmoryLayoutWidgetDto Widget(
        string idSuffix,
        ArmoryWidgetType type,
        int x, int y, int w, int h,
        int? newsSettings = null,
        int? recentSettings = null)
    {
        Dictionary<string, JsonElement>? settings = null;
        if (type == ArmoryWidgetType.News || type == ArmoryWidgetType.NewsFeed)
        {
            settings = new Dictionary<string, JsonElement>
            {
                ["limit"] = JsonSerializer.SerializeToElement(newsSettings ?? 3),
                ["title"] = JsonSerializer.SerializeToElement("Latest News"),
                ["showViewAll"] = JsonSerializer.SerializeToElement(true),
            };
        }
        else if (type == ArmoryWidgetType.RecentCharacters)
        {
            settings = new Dictionary<string, JsonElement>
            {
                ["limit"] = JsonSerializer.SerializeToElement(recentSettings ?? 5),
                ["title"] = JsonSerializer.SerializeToElement("Recently Active"),
            };
        }
        else if (type == ArmoryWidgetType.CharacterSearch)
        {
            settings = new Dictionary<string, JsonElement>
            {
                ["pageLength"] = JsonSerializer.SerializeToElement(50),
            };
        }
        else if (type == ArmoryWidgetType.Spacer)
        {
            settings = new Dictionary<string, JsonElement>
            {
                ["height"] = JsonSerializer.SerializeToElement(h),
            };
        }

        return new ArmoryLayoutWidgetDto
        {
            Id = $"tpl-{idSuffix}",
            Type = type,
            X = x,
            Y = y,
            W = w,
            H = h,
            Visible = true,
            Settings = settings
        };
    }

    private static void CompactWidgetsVertically(List<ArmoryLayoutWidgetDto> widgets, int columns)
    {
        var placed = new List<ArmoryLayoutWidgetDto>();
        foreach (var widget in widgets.Where(w => w.Visible).OrderBy(w => w.Y).ThenBy(w => w.X))
        {
            var y = 0;
            while (!CanPlace(widget, y, placed, columns))
            {
                y++;
            }

            widget.Y = y;
            placed.Add(widget);
        }
    }

    private static bool CanPlace(ArmoryLayoutWidgetDto widget, int y, List<ArmoryLayoutWidgetDto> placed, int columns)
    {
        if (widget.X + widget.W > columns)
        {
            return false;
        }

        foreach (var other in placed)
        {
            if (Overlaps(widget.X, y, widget.W, widget.H, other.X, other.Y, other.W, other.H))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Overlaps(int x1, int y1, int w1, int h1, int x2, int y2, int w2, int h2) =>
        x1 < x2 + w2 && x1 + w1 > x2 && y1 < y2 + h2 && y1 + h1 > y2;

    private static int MinWidth(ArmoryWidgetType type) => type switch
    {
        ArmoryWidgetType.PageTitle => 3,
        ArmoryWidgetType.Spacer => 1,
        ArmoryWidgetType.CharacterSubnav => 6,
        ArmoryWidgetType.MapCanvas => 6,
        _ => 3
    };

    private static int MinHeight(ArmoryWidgetType type) => type switch
    {
        ArmoryWidgetType.PageTitle => 1,
        ArmoryWidgetType.Spacer => 1,
        ArmoryWidgetType.CharacterHeader => 2,
        ArmoryWidgetType.CharacterSubnav => 2,
        ArmoryWidgetType.CharacterModelViewer => 4,
        ArmoryWidgetType.CharacterStats => 3,
        ArmoryWidgetType.CharacterOverviewCards => 2,
        ArmoryWidgetType.News => 2,
        ArmoryWidgetType.RecentCharacters => 2,
        ArmoryWidgetType.CharacterSearch => 3,
        ArmoryWidgetType.NewsFeed => 3,
        ArmoryWidgetType.GuildRoster => 4,
        ArmoryWidgetType.TopLogsTable => 4,
        ArmoryWidgetType.MapCanvas => 6,
        ArmoryWidgetType.ConnectCta => 2,
        _ => 1
    };
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzerothPlatform.Core.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArmoryLayoutMode
{
    Template,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArmoryLayoutTemplateId
{
    Default,
    NewsFocus,
    CharactersFocus,
    Dashboard,
    WowheadProfile,
    AowowDense,
    IcyVeinsHero,
    WowheadTabs,
    SingleColumn,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArmoryWidgetType
{
    PageTitle,
    News,
    RecentCharacters,
    RealmSelector,
    CharacterSearch,
    Spacer,
    CharacterHeader,
    CharacterModelViewer,
    CharacterStats,
    CharacterOverviewCards,
    CharacterSubnav,
    ConnectCta,
    NewsFeed,
    GuildHeader,
    GuildRoster,
    TopLogsTable,
    MapCanvas,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WidgetShadowPreset
{
    None,
    Sm,
    Md,
    Lg,
    Theme
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArmoryNavbarLinkKind
{
    Home,
    TopLogs,
    Map,
    Connect,
    News,
    Custom
}

/// <summary>Per-widget visual chrome. Unset fields inherit from the global armory theme.</summary>
public sealed class WidgetChromeDto
{
    public bool? BorderEnabled { get; set; }
    /// <summary>Hex color or "theme" to use --armory-border.</summary>
    public string? BorderColor { get; set; }
    public int? BorderWidth { get; set; }
    public int? BorderRadius { get; set; }
    /// <summary>Hex color, "theme", or "transparent".</summary>
    public string? BackgroundColor { get; set; }
    public int? Padding { get; set; }
    public WidgetShadowPreset? Shadow { get; set; }
    public string? TitleColor { get; set; }
}

public sealed class ArmoryLayoutGridDto
{
    public int Columns { get; set; } = 12;
    public int RowHeight { get; set; } = 48;
    public int Gap { get; set; } = 12;
}

public sealed class ArmoryLayoutWidgetDto
{
    public string Id { get; set; } = string.Empty;
    public ArmoryWidgetType Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 4;
    public int H { get; set; } = 2;
    public bool Visible { get; set; } = true;
    public Dictionary<string, JsonElement>? Settings { get; set; }
    public WidgetChromeDto? Chrome { get; set; }
}

public sealed class ArmoryNavbarLinkDto
{
    public string Id { get; set; } = string.Empty;
    public ArmoryNavbarLinkKind Kind { get; set; } = ArmoryNavbarLinkKind.Custom;
    public bool Visible { get; set; } = true;
    /// <summary>Override label for built-in links; required for custom links.</summary>
    public string? Label { get; set; }
    /// <summary>Relative (e.g. /news) or absolute URL for custom links.</summary>
    public string? Href { get; set; }
    public bool OpenInNewTab { get; set; }
}

public sealed class ArmoryNavbarDto
{
    public bool ShowSearch { get; set; } = true;
    public string SearchPlaceholder { get; set; } = "Search character...";
    public List<ArmoryNavbarLinkDto> Links { get; set; } = [];
}

/// <summary>Grid layout for a single armory page.</summary>
public sealed class ArmoryPageLayoutDto
{
    public ArmoryLayoutMode Mode { get; set; } = ArmoryLayoutMode.Template;
    public string TemplateId { get; set; } = "Default";
    public ArmoryLayoutGridDto Grid { get; set; } = new();
    public List<ArmoryLayoutWidgetDto> Widgets { get; set; } = [];
}

/// <summary>Per-stack armory site layout (V2): navbar + per-page widget grids.</summary>
public sealed class ArmoryLayoutDto
{
    public int Version { get; set; } = 2;
    public ArmoryNavbarDto Navbar { get; set; } = new();
    public Dictionary<string, ArmoryPageLayoutDto> Pages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>V1 root grid — read for migration only; not written after normalize.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArmoryLayoutMode? Mode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArmoryLayoutTemplateId? TemplateId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArmoryLayoutGridDto? Grid { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ArmoryLayoutWidgetDto>? Widgets { get; set; }
}

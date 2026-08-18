using System.Text.Json.Serialization;

namespace AzerothPlatform.Core.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArmoryStyleTemplate
{
    Classic,
    Tbc,
    Wotlk,
    Custom
}

/// <summary>Per-stack armory theme settings. Classic without advanced styling leaves the armory unchanged.</summary>
public sealed class ArmoryStylingDto
{
    public ArmoryStyleTemplate Template { get; set; } = ArmoryStyleTemplate.Classic;
    public bool AdvancedEnabled { get; set; }
    public string PrimaryColor { get; set; } = "#1d4ed8";
    public string SecondaryColor { get; set; } = "#334155";
    public string AccentColor { get; set; } = "#f59e0b";
    public string BackgroundColor { get; set; } = "#0f172a";
    public string SurfaceColor { get; set; } = "#111827";
    public string PanelColor { get; set; } = "#1f2937";
    public string BorderColor { get; set; } = "#334155";
    public string NavbarColor { get; set; } = "#111827";
    public string LinkColor { get; set; } = "#f59e0b";
    public string HeadingColor { get; set; } = "#f8fafc";
    public string MutedTextColor { get; set; } = "#cbd5e1";
    public string InputColor { get; set; } = "#0f172a";
    public string ButtonTextColor { get; set; } = "#ffffff";
    public string TextColor { get; set; } = "#f8fafc";
    public string? WallpaperUrl { get; set; }
}

using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Infrastructure.Services;

/// <summary>
/// Central palette + CSS generator for stack armory themes. The generated CSS redefines
/// the <c>--armory-*</c> CSS custom properties consumed by the bundled <c>theme.css</c>
/// so the entire armory re-colours via variable cascade — no per-selector overrides needed.
/// </summary>
internal static class ArmoryStylingTheme
{
    private const string ClassicPrimary = "#8a5a24";
    private const string ClassicSecondary = "#3a2412";
    private const string ClassicAccent = "#d8a84f";
    private const string ClassicBackground = "#1b1209";
    private const string ClassicSurface = "#2a1a0c";
    private const string ClassicPanel = "#2b2114";
    private const string ClassicBorder = "#5a4628";
    private const string ClassicNavbar = "#241408";
    private const string ClassicLink = "#f4d68a";
    private const string ClassicHeading = "#ffd980";
    private const string ClassicMutedText = "#b3a384";
    private const string ClassicInput = "#1c1209";
    private const string ClassicButtonText = "#fff3d1";
    private const string ClassicText = "#e8dcc4";

    private const string TbcPrimary = "#4c8f2f";
    private const string TbcSecondary = "#1a1a1a";
    private const string TbcAccent = "#a4ff2f";
    private const string TbcBackground = "#080808";
    private const string TbcSurface = "#141414";
    private const string TbcPanel = "#1a1a1a";
    private const string TbcBorder = "#3a6b1f";
    private const string TbcNavbar = "#0d0d0d";
    private const string TbcLink = "#8cff4d";
    private const string TbcHeading = "#c8ff9e";
    private const string TbcMutedText = "#9bbf8f";
    private const string TbcInput = "#101010";
    private const string TbcButtonText = "#0a1a03";
    private const string TbcText = "#e4f7de";

    private const string WotlkPrimary = "#2f6f9e";
    private const string WotlkSecondary = "#1c2a35";
    private const string WotlkAccent = "#8fe3ff";
    private const string WotlkBackground = "#070d12";
    private const string WotlkSurface = "#101a22";
    private const string WotlkPanel = "#152230";
    private const string WotlkBorder = "#356a8c";
    private const string WotlkNavbar = "#0a1219";
    private const string WotlkLink = "#a9e6ff";
    private const string WotlkHeading = "#e6fbff";
    private const string WotlkMutedText = "#9db9c9";
    private const string WotlkInput = "#0c151d";
    private const string WotlkButtonText = "#eafaff";
    private const string WotlkText = "#dceef7";

    public static ArmoryStylingDto DefaultFor(ArmoryStyleTemplate template) => template switch
    {
        ArmoryStyleTemplate.Tbc => Create(
            ArmoryStyleTemplate.Tbc,
            primary: TbcPrimary,
            secondary: TbcSecondary,
            accent: TbcAccent,
            background: TbcBackground,
            surface: TbcSurface,
            panel: TbcPanel,
            border: TbcBorder,
            navbar: TbcNavbar,
            link: TbcLink,
            heading: TbcHeading,
            muted: TbcMutedText,
            input: TbcInput,
            buttonText: TbcButtonText,
            text: TbcText),
        ArmoryStyleTemplate.Wotlk => Create(
            ArmoryStyleTemplate.Wotlk,
            primary: WotlkPrimary,
            secondary: WotlkSecondary,
            accent: WotlkAccent,
            background: WotlkBackground,
            surface: WotlkSurface,
            panel: WotlkPanel,
            border: WotlkBorder,
            navbar: WotlkNavbar,
            link: WotlkLink,
            heading: WotlkHeading,
            muted: WotlkMutedText,
            input: WotlkInput,
            buttonText: WotlkButtonText,
            text: WotlkText),
        ArmoryStyleTemplate.Custom => Create(ArmoryStyleTemplate.Custom, advanced: true),
        _ => Create(ArmoryStyleTemplate.Classic),
    };

    public static ArmoryStylingDto Normalize(ArmoryStylingDto styling)
    {
        var template = Enum.IsDefined(styling.Template) ? styling.Template : ArmoryStyleTemplate.Classic;
        var defaults = DefaultFor(template);
        return new ArmoryStylingDto
        {
            Template = template,
            AdvancedEnabled = styling.AdvancedEnabled || template == ArmoryStyleTemplate.Custom,
            PrimaryColor = NormalizeColor(styling.PrimaryColor, defaults.PrimaryColor),
            SecondaryColor = NormalizeColor(styling.SecondaryColor, defaults.SecondaryColor),
            AccentColor = NormalizeColor(styling.AccentColor, defaults.AccentColor),
            BackgroundColor = NormalizeColor(styling.BackgroundColor, defaults.BackgroundColor),
            SurfaceColor = NormalizeColor(styling.SurfaceColor, defaults.SurfaceColor),
            PanelColor = NormalizeColor(styling.PanelColor, defaults.PanelColor),
            BorderColor = NormalizeColor(styling.BorderColor, defaults.BorderColor),
            NavbarColor = NormalizeColor(styling.NavbarColor, defaults.NavbarColor),
            LinkColor = NormalizeColor(styling.LinkColor, defaults.LinkColor),
            HeadingColor = NormalizeColor(styling.HeadingColor, defaults.HeadingColor),
            MutedTextColor = NormalizeColor(styling.MutedTextColor, defaults.MutedTextColor),
            InputColor = NormalizeColor(styling.InputColor, defaults.InputColor),
            ButtonTextColor = NormalizeColor(styling.ButtonTextColor, defaults.ButtonTextColor),
            TextColor = NormalizeColor(styling.TextColor, defaults.TextColor),
            WallpaperUrl = string.IsNullOrWhiteSpace(styling.WallpaperUrl) ? null : styling.WallpaperUrl,
        };
    }

    /// <summary>
    /// Generates <c>azp-theme.css</c> that redefines the <c>--armory-*</c> CSS custom properties
    /// on <c>:root</c>. The bundled <c>theme.css</c> consumes these variables in every rule, so
    /// this single block of variable overrides re-themes the entire armory with zero selector
    /// specificity battles.
    /// </summary>
    public static string BuildCss(ArmoryStylingDto styling)
    {
        var colors = styling.AdvancedEnabled || styling.Template == ArmoryStyleTemplate.Custom
            ? Normalize(styling)
            : DefaultFor(styling.Template);
        var wallpaperUrl = string.IsNullOrWhiteSpace(styling.WallpaperUrl)
            ? DefaultTemplateWallpaperUrl(styling.Template)
            : styling.WallpaperUrl;

        if (styling.Template == ArmoryStyleTemplate.Classic && !styling.AdvancedEnabled && string.IsNullOrWhiteSpace(wallpaperUrl))
        {
            return string.Empty;
        }

        return $$"""
/* Generated by AzerothPlatform. Edit from Stack -> Armory -> Styling. */
:root {
  --armory-primary: {{colors.PrimaryColor}};
  --armory-secondary: {{colors.SecondaryColor}};
  --armory-accent: {{colors.AccentColor}};
  --armory-bg: {{colors.BackgroundColor}};
  --armory-surface: {{colors.SurfaceColor}};
  --armory-panel: {{colors.PanelColor}};
  --armory-border: {{colors.BorderColor}};
  --armory-navbar: {{colors.NavbarColor}};
  --armory-link: {{colors.LinkColor}};
  --armory-heading: {{colors.HeadingColor}};
  --armory-text-muted: {{colors.MutedTextColor}};
  --armory-input: {{colors.InputColor}};
  --armory-button-text: {{colors.ButtonTextColor}};
  --armory-text: {{colors.TextColor}};
  --armory-wallpaper-overlay: linear-gradient(rgba(0, 0, 0, 0.72), rgba(0, 0, 0, 0.72));
  --armory-panel-highlight: color-mix(in srgb, var(--armory-panel) 55%, var(--armory-border));
  --armory-border-bright: color-mix(in srgb, var(--armory-border) 55%, var(--armory-accent));
}
""";
    }

    private static ArmoryStylingDto Create(
        ArmoryStyleTemplate template,
        string primary = ClassicPrimary,
        string secondary = ClassicSecondary,
        string accent = ClassicAccent,
        string background = ClassicBackground,
        string surface = ClassicSurface,
        string panel = ClassicPanel,
        string border = ClassicBorder,
        string navbar = ClassicNavbar,
        string link = ClassicLink,
        string heading = ClassicHeading,
        string muted = ClassicMutedText,
        string input = ClassicInput,
        string buttonText = ClassicButtonText,
        string text = ClassicText,
        bool advanced = false)
        => new()
        {
            Template = template,
            AdvancedEnabled = advanced,
            PrimaryColor = primary,
            SecondaryColor = secondary,
            AccentColor = accent,
            BackgroundColor = background,
            SurfaceColor = surface,
            PanelColor = panel,
            BorderColor = border,
            NavbarColor = navbar,
            LinkColor = link,
            HeadingColor = heading,
            MutedTextColor = muted,
            InputColor = input,
            ButtonTextColor = buttonText,
            TextColor = text,
        };

    private static string NormalizeColor(string? value, string fallback)
    {
        var color = (value ?? string.Empty).Trim();
        return color.Length == 7 && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit)
            ? color.ToLowerInvariant()
            : fallback;
    }

    private static string? DefaultTemplateWallpaperUrl(ArmoryStyleTemplate template) => template switch
    {
        ArmoryStyleTemplate.Classic => "/img/bg/wallpaper_classic.jpg",
        ArmoryStyleTemplate.Tbc => "/img/bg/wallpaper_tbc.jpg",
        ArmoryStyleTemplate.Wotlk => "/img/bg/wallpaper_wotlk.jpg",
        _ => null,
    };
}

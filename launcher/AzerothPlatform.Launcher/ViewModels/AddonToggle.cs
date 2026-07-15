using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzerothPlatform.Launcher.ViewModels;

/// <summary>A single addon shown in the launcher with an enable/disable toggle.</summary>
public partial class AddonToggle : ObservableObject
{
    private readonly Action<AddonToggle, bool> _onToggle;

    public AddonToggle(string name, bool enabled, Action<AddonToggle, bool> onToggle)
    {
        Name = name;
        _isEnabled = enabled;
        _onToggle = onToggle;
    }

    public string Name { get; }

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value) => _onToggle(this, value);
}

/// <summary>Which of the news sub-views the Play tab currently shows.</summary>
public enum NewsViewMode
{
    /// <summary>Horizontal strip of up to 4 cover cards + a "View all" card.</summary>
    List,

    /// <summary>Full-article reading view (WebView2-rendered HTML).</summary>
    Reading,

    /// <summary>3-column grid of every article.</summary>
    Grid
}

/// <summary>A rich news/patch-note article shown in the launcher.</summary>
public sealed class NewsItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;

    /// <summary>Sanitized HTML body (rendered in the reading view).</summary>
    public string Html { get; set; } = string.Empty;

    /// <summary>Absolute cover-image URL (used by the reading-view HTML), or null.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Downloaded cover thumbnail for the card/grid, or null.</summary>
    public Bitmap? Cover { get; set; }

    public bool HasCover => Cover is not null;
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AzerothPlatform.Launcher.ViewModels;

namespace AzerothPlatform.Launcher.Views;

public partial class MainWindow : Window
{
    // HTML waiting to be rendered once the WebView's native adapter is ready.
    private string? _pendingNewsHtml;

    // The news WebView is created on first use rather than at startup. On Windows the native web
    // control initializes the WebView2 runtime on attach; deferring + guarding it means a machine
    // without that runtime still opens the launcher (only the in-app reader degrades).
    private NativeWebView? _newsWebView;
    private bool _webViewUnavailable;

    public MainWindow()
    {
        InitializeComponent();
        TrySetAppIcon();
    }

    /// <summary>
    /// Creates the news WebView the first time it is needed, tolerating platforms where the native
    /// web runtime is missing (shows an inline message instead of crashing).
    /// </summary>
    private void EnsureNewsWebView()
    {
        if (_newsWebView is not null || _webViewUnavailable)
        {
            return;
        }

        try
        {
            _newsWebView = new NativeWebView();
            // The native adapter is (re)created when the control attaches; render the pending HTML then.
            _newsWebView.AdapterCreated += (_, _) => NavigatePendingNews();
            NewsWebViewHost.Child = _newsWebView;
        }
        catch (Exception ex)
        {
            _webViewUnavailable = true;
            CrashLog.Write(ex, "NativeWebView init");
            NewsWebViewHost.Child = new TextBlock
            {
                Text = "The in-app news reader isn't available on this system.",
                Foreground = new SolidColorBrush(Color.Parse("#B3A384")),
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

    /// <summary>
    /// Uses the baked global app icon (AppIcon.ico, bundled next to the exe when configured on the
    /// website) as the window/taskbar icon. No icon shipped → keep the framework default.
    /// </summary>
    private void TrySetAppIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                Icon = new WindowIcon(iconPath);
            }
        }
        catch
        {
            // A bad/missing icon must never prevent the launcher from opening.
        }
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is MainWindowViewModel vm)
        {
            vm.PickFolderAsync = PickFolderAsync;
            vm.RequestShutdown = () =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            };

            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.ReadingHtml))
                {
                    _pendingNewsHtml = vm.ReadingHtml;
                    EnsureNewsWebView();
                    NavigatePendingNews();
                }
            };

            await vm.InitializeAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.StopBackgroundWork();
        }

        base.OnClosed(e);
    }

    /// <summary>Renders the pending reading-view HTML if both the HTML and the adapter are ready.</summary>
    private void NavigatePendingNews()
    {
        if (string.IsNullOrEmpty(_pendingNewsHtml) || _newsWebView is null)
        {
            return;
        }

        try
        {
            _newsWebView.NavigateToString(_pendingNewsHtml);
        }
        catch
        {
            // The adapter may not be ready yet; AdapterCreated will retry.
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the WoW client install folder",
            AllowMultiple = false
        });

        if (folders.Count == 0)
        {
            return null;
        }

        return folders[0].TryGetLocalPath();
    }
}

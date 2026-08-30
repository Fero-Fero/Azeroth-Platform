using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AzerothPlatform.Launcher.Services;
using AzerothPlatform.Launcher.ViewModels;
using AzerothPlatform.Launcher.Views;

namespace AzerothPlatform.Launcher;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var stateStore = new LauncherStateStore();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(stateStore)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

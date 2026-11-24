using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AutoKey.Platform;

namespace AutoKey;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            IPlatform platform = PlatformFactory.Create();
            var window = new MainWindow(platform);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => platform.Dispose();
            AddTrayIcon(desktop, window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The tray icon (in the menu bar on macOS) that appears while the main window is hidden. Clicking it, or Show
    /// AutoKey in its menu, brings the window back; Quit AutoKey in the menu ends the app.
    /// </summary>
    private void AddTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        var showItem = new NativeMenuItem("Show AutoKey");
        var quitItem = new NativeMenuItem("Quit AutoKey");
        var trayIcon = new TrayIcon
        {
            Icon = window.Icon,
            ToolTipText = "AutoKey",
            IsVisible = false,
            Menu = new NativeMenu { showItem, new NativeMenuItemSeparator(), quitItem },
        };

        // Avalonia only creates the icon once it belongs to the application (and removes it when the app exits).
        TrayIcon.SetIcons(this, new TrayIcons { trayIcon });

        window.HiddenToTray += (_, _) => trayIcon.IsVisible = true;

        void ShowWindow()
        {
            trayIcon.IsVisible = false;
            window.ShowFromTray();
        }

        // Each action is queued on the UI thread: Linux reports tray clicks from D-Bus, and a menu item shouldn't
        // close the app or open a window in the middle of the menu's own click handling. On macOS a click on the
        // icon opens the menu rather than raising Clicked, hence Show AutoKey.
        trayIcon.Clicked += (_, _) => Dispatcher.UIThread.Post(ShowWindow);
        showItem.Click += (_, _) => Dispatcher.UIThread.Post(ShowWindow);
        quitItem.Click += (_, _) => Dispatcher.UIThread.Post(() => desktop.Shutdown());

        // macOS: clicking AutoKey in the Dock brings the window back too.
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                {
                    Dispatcher.UIThread.Post(ShowWindow);
                }
            };
        }
    }
}

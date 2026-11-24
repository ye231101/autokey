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
    /// The tray icon (in the menu bar on macOS), shown for as long as AutoKey runs. Clicking it, or Open in its menu,
    /// brings the window to the front, and back if it was closed to the tray; About in the menu opens the About
    /// window, and Exit ends the app. On Windows the menu is drawn by Avalonia, so App.axaml styles it; macOS and
    /// Linux show it as a native menu.
    /// </summary>
    private void AddTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        var showItem = new NativeMenuItem("Open");
        var aboutItem = new NativeMenuItem("About");
        var quitItem = new NativeMenuItem("Exit");
        var trayIcon = new TrayIcon
        {
            Icon = window.Icon,
            ToolTipText = "AutoKey",
            Menu = new NativeMenu { showItem, new NativeMenuItemSeparator(), aboutItem, quitItem },
        };

        // Avalonia only creates the icon once it belongs to the application (and removes it when the app exits).
        TrayIcon.SetIcons(this, new TrayIcons { trayIcon });

        // Each action is queued on the UI thread: Linux reports tray clicks from D-Bus, and a menu item shouldn't
        // close the app or open a window in the middle of the menu's own click handling. On macOS a click on the
        // icon opens the menu rather than raising Clicked, hence Open.
        trayIcon.Clicked += (_, _) => Dispatcher.UIThread.Post(window.ShowFromTray);
        showItem.Click += (_, _) => Dispatcher.UIThread.Post(window.ShowFromTray);
        aboutItem.Click += (_, _) => Dispatcher.UIThread.Post(window.ShowAbout);
        quitItem.Click += (_, _) => Dispatcher.UIThread.Post(() => desktop.Shutdown());

        // macOS: clicking AutoKey in the Dock brings the window back too.
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                {
                    Dispatcher.UIThread.Post(window.ShowFromTray);
                }
            };
        }
    }
}

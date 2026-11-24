using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AutoKey.Platform;
using AutoKey.Services;
using AutoKey.ViewModels;

namespace AutoKey;

public partial class MainWindow : Window
{
    private readonly IPlatform _platform;
    private readonly MainViewModel _viewModel;
    private OverlayWindow? _overlay;
    private AboutWindow? _about;

    internal MainWindow(IPlatform platform)
    {
        _platform = platform;
        InitializeComponent();

        _viewModel = new MainViewModel(AppSettings.Load(), platform, ReadClipboardTextAsync);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = _viewModel;

        platform.TrackWindow(this);
        platform.Hotkeys.Unavailable += () => Dispatcher.UIThread.Post(() => _viewModel.SetToggleHotkeyAvailable(false));
        Opened += OnOpened;
        Activated += (_, _) => _viewModel.RefreshNotice(); // e.g. after granting a permission in System Settings
    }

    /// <summary>
    /// Shows the window in front of other windows, for the tray icon: brings it back if it was closed to the tray, and
    /// restores it if it's minimized.
    /// </summary>
    internal void ShowFromTray() => BringToFront(this);

    /// <summary>Shows the About window in front of other windows, opening it if it isn't open yet.</summary>
    internal void ShowAbout()
    {
        if (_about is null)
        {
            _about = new AboutWindow();
            _platform.TrackWindow(_about);
            _about.Closed += (_, _) => _about = null;
        }

        BringToFront(_about);
    }

    private void OnAboutClick(object? sender, RoutedEventArgs e) => ShowAbout();

    private static void BringToFront(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal; // e.g. the main window was closed from the taskbar while minimized
        }

        window.Activate();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // Closing the window (its close button, Alt+F4, the taskbar) only hides AutoKey: the hotkeys keep working and
        // typing carries on. Quitting from the tray menu or the macOS app menu, or signing out, still closes it. On
        // a desktop without a tray (GNOME without the AppIndicator extension) there'd be no way back, so it quits.
        if (e.Cancel || e.CloseReason != WindowCloseReason.WindowClosing || e.IsProgrammatic || !_platform.IsTrayAvailable)
        {
            return;
        }

        e.Cancel = true;
        _viewModel.SaveSettings(); // AutoKey may now stay in the tray for a long time
        Hide();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened; // also raised each time the window comes back from the tray; this setup is needed once

        // Fit on smaller screens.
        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            double available = (screen.WorkingArea.Height / screen.Scaling) - 40;
            if (Height > available)
            {
                Height = Math.Max(MinHeight, available);
            }
        }

        bool registered = _platform.Hotkeys.Register(HotkeyAction.ToggleTyping, () => Dispatcher.UIThread.Post(_viewModel.OnToggleHotkey));
        _viewModel.SetToggleHotkeyAvailable(registered);
        InputBox.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _platform.Hotkeys.Unregister(HotkeyAction.ToggleTyping);
        _platform.Hotkeys.Unregister(HotkeyAction.Stop);
        _viewModel.Dispose(); // stops any typing (releasing held keys) and saves settings
        _overlay?.Close();
        _about?.Close();
        base.OnClosed(e);
    }

    private async Task<string?> ReadClipboardTextAsync() =>
        Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsSessionActive):
                // The stop hotkey (Esc) is only claimed while there's something to stop.
                if (_viewModel.IsSessionActive)
                {
                    _platform.Hotkeys.Register(HotkeyAction.Stop, () => Dispatcher.UIThread.Post(_viewModel.OnStopHotkey));
                }
                else
                {
                    _platform.Hotkeys.Unregister(HotkeyAction.Stop);
                }

                break;

            case nameof(MainViewModel.IsOverlayVisible):
                if (_viewModel.IsOverlayVisible)
                {
                    _overlay ??= CreateOverlay();
                    _overlay.Show();
                }
                else
                {
                    _overlay?.Hide();
                }

                break;
        }
    }

    private OverlayWindow CreateOverlay()
    {
        var overlay = new OverlayWindow { DataContext = _viewModel };
        _platform.TrackWindow(overlay);
        _platform.MakeNonActivating(overlay);
        return overlay;
    }
}

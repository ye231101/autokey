using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
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

    /// <summary>Raised when closing the window only hid it; the app then shows a tray icon to bring it back.</summary>
    internal event EventHandler? HiddenToTray;

    /// <summary>Shows the window again after it was hidden to the tray, in front of other windows.</summary>
    internal void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal; // it was closed from the taskbar while minimized
        }

        Activate();
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
        HiddenToTray?.Invoke(this, EventArgs.Empty);
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

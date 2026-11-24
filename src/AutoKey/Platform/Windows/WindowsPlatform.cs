using System.Runtime.Versioning;
using Avalonia.Controls;
using AutoKey.Core.Execution;
using AutoKey.Core.Keyboard;
using static AutoKey.Platform.Windows.NativeMethods;

namespace AutoKey.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsPlatform : IPlatform
{
    private readonly Win32Hotkeys _hotkeys = new();

    public bool SupportsTypingMethods => true;

    public bool DetectsCodeEditors => true;

    public bool IsTrayAvailable => true;

    public IDesktop Desktop { get; } = new Win32Desktop();

    public IGlobalHotkeys Hotkeys => _hotkeys;

    public IKeyboardDevice CreateKeyboard(TypingMethod method, bool shiftEnterForNewLines) =>
        new Win32KeyboardDevice(method, shiftEnterForNewLines);

    public KeyboardGeometry GetKeyboardGeometry() => SystemKeyboardGeometry.ForCurrentLayout();

    public IDisposable BeginTyping() => new TimerResolution();

    // Characters missing from the keyboard layout are sent as Unicode, so everything can be typed.
    public IReadOnlyCollection<string> FindUntypeableCharacters(string text) => [];

    public PlatformNotice? GetNotice() => null;

    public string DescribeBlockedTarget(string? targetTitle, string resumeHint) =>
        $"{(targetTitle is null ? "This window" : $"“{targetTitle}”")} runs as administrator, so Windows blocks AutoKey's keystrokes. " +
        $"Run AutoKey as administrator to type into it, or switch to another window and {resumeHint}.";

    public void TrackWindow(Window window)
    {
        // Win32Desktop recognises AutoKey's windows by process id; here we only keep the title bar in step with the
        // light/dark theme (Windows 10 doesn't do that for us).
        ApplyTitleBarTheme(window, repaint: false);
        window.ActualThemeVariantChanged += (_, _) => ApplyTitleBarTheme(window, repaint: true);
    }

    private static void ApplyTitleBarTheme(Window window, bool repaint)
    {
        int build = Environment.OSVersion.Version.Build;
        if (build < 17763 || window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0)
        {
            return; // dark title bars arrived in Windows 10 1809
        }

        int dark = window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? 1 : 0;
        int attribute = build >= 18985 ? DWMWA_USE_IMMERSIVE_DARK_MODE : DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1;
        _ = DwmSetWindowAttribute(hwnd, attribute, ref dark, sizeof(int));
        if (repaint)
        {
            SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    public void MakeNonActivating(Window window)
    {
        // WS_EX_NOACTIVATE: clicking the window doesn't take focus away from the window being typed into.
        // WS_EX_TOOLWINDOW: no taskbar button and no Alt+Tab entry.
        Win32Properties.AddWindowStylesCallback(window, (style, exStyle) =>
            (style, exStyle | (uint)WS_EX_NOACTIVATE | (uint)WS_EX_TOOLWINDOW));
        Win32Properties.AddWndProcHookCallback(window, (nint hwnd, uint msg, nint wParam, nint lParam, ref bool handled) =>
        {
            if (msg == WM_MOUSEACTIVATE)
            {
                handled = true;
                return MA_NOACTIVATE;
            }

            return 0;
        });
    }

    public void Dispose() => _hotkeys.Dispose();
}

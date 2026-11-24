using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Controls;
using AutoKey.Core.Execution;
using AutoKey.Core.Keyboard;
using SharpHook.Data;
using SharpHook.Providers;
using SharpHook.Simulation;

namespace AutoKey.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal sealed class MacPlatform : IPlatform
{
    private const string AccessibilitySettings = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    // F8 is the play/pause media key on Mac keyboards, so the start/pause hotkey is Control-Option-P instead.
    private readonly HookHotkeys _hotkeys = new(
        new HookHotkeys.Binding(KeyCode.VcP, EventMask.Ctrl | EventMask.Alt, "⌃⌥P"),
        new HookHotkeys.Binding(KeyCode.VcEscape, EventMask.None, "Esc"));

    private readonly Lazy<IEventSimulator> _simulator = new(() => EventSimulator.Create("AutoKey"));

    public bool SupportsTypingMethods => false;

    // The tray icon goes in the menu bar, which is always there.
    public bool IsTrayAvailable => true;

    public IDesktop Desktop { get; } = new MacDesktop();

    public IGlobalHotkeys Hotkeys => _hotkeys;

    /// <summary>Whether macOS allows AutoKey to send keystrokes (Privacy &amp; Security › Accessibility).</summary>
    internal static bool HasAccessibilityPermission => UioHookProvider.Instance.IsAxApiEnabled(false);

    // Quartz events carry Unicode text directly, so any character can be typed.
    public IKeyboardDevice CreateKeyboard(TypingMethod method, bool shiftEnterForNewLines) =>
        new HookKeyboardDevice(_simulator.Value, shiftEnterForNewLines, canTypeText: true, findKey: null, MacNative.UserHoldsModifiers);

    public KeyboardGeometry GetKeyboardGeometry() => KeyboardGeometry.UsQwerty;

    public IDisposable BeginTyping() => NoOpScope.Instance;

    public IReadOnlyCollection<string> FindUntypeableCharacters(string text) => [];

    public PlatformNotice? GetNotice() => HasAccessibilityPermission
        ? null
        : new PlatformNotice(
            "Allow AutoKey to type for you",
            "macOS needs your permission before AutoKey can press keys. Turn on AutoKey in System Settings › " +
            "Privacy & Security › Accessibility, then quit and reopen AutoKey.",
            "Open Accessibility settings",
            OpenAccessibilitySettings);

    public string DescribeBlockedTarget(string? targetTitle, string resumeHint) => HasAccessibilityPermission
        ? "macOS secure input is on (a password field, or Terminal's Secure Keyboard Entry), which blocks typing " +
          $"from other apps. Leave that field and {resumeHint}."
        : "AutoKey doesn't have permission to press keys yet. Turn it on in System Settings › Privacy & Security › " +
          "Accessibility, then quit and reopen AutoKey.";

    public void TrackWindow(Window window)
    {
        // MacDesktop recognises AutoKey by its process id.
    }

    public void MakeNonActivating(Window window)
    {
        // Avalonia windows always activate the app on macOS; the focus guard then pauses typing, which is safe.
    }

    public void Dispose()
    {
        _hotkeys.Dispose();
        if (_simulator.IsValueCreated)
        {
            _simulator.Value.Dispose();
        }
    }

    private static void OpenAccessibilitySettings()
    {
        // Asking once also adds AutoKey to the list in System Settings, so the user only has to flip the switch.
        UioHookProvider.Instance.IsAxApiEnabled(true);
        Process.Start(new ProcessStartInfo("open", AccessibilitySettings) { UseShellExecute = false });
    }
}

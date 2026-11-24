using Avalonia.Controls;
using AutoKey.Core.Execution;
using AutoKey.Core.Keyboard;

namespace AutoKey.Platform;

/// <summary>How characters are sent to other applications (only Windows offers a choice).</summary>
public enum TypingMethod
{
    /// <summary>Press the real keys (with Shift where needed) for characters on the keyboard layout; send the rest as Unicode.</summary>
    Keyboard,

    /// <summary>Send every character as Unicode input, independent of the keyboard layout.</summary>
    Unicode,
}

public enum HotkeyAction
{
    /// <summary>Start typing into the focused window, or pause/resume.</summary>
    ToggleTyping,

    /// <summary>Stop typing.</summary>
    Stop,
}

/// <summary>Something about this computer that limits what AutoKey can do, shown as a banner.</summary>
public sealed record PlatformNotice(string Title, string Message, string? ActionLabel = null, Action? Action = null);

/// <summary>System-wide hotkeys that work while other applications have focus.</summary>
public interface IGlobalHotkeys : IDisposable
{
    /// <summary>How the hotkey is written on this platform, for example "F8" or "⌃⌥P".</summary>
    string Label(HotkeyAction action);

    /// <summary>Registers the hotkey. Returns false if it isn't available. The callback may run on any thread.</summary>
    bool Register(HotkeyAction action, Action callback);

    void Unregister(HotkeyAction action);

    /// <summary>Raised (on any thread) if the hotkeys stop working, for example because a permission is missing.</summary>
    event Action? Unavailable;
}

/// <summary>Everything AutoKey needs from the operating system.</summary>
public interface IPlatform : IDisposable
{
    /// <summary>Whether the user can choose a <see cref="TypingMethod"/>.</summary>
    bool SupportsTypingMethods { get; }

    /// <summary>Whether <see cref="IDesktop.IsCodeEditor"/> can recognise code editors on this platform.</summary>
    bool DetectsCodeEditors => false;

    /// <summary>
    /// Whether the desktop can show AutoKey's tray icon right now (in the menu bar on macOS). Closing the window only
    /// hides AutoKey while it can, so there's always a way to bring the window back.
    /// </summary>
    bool IsTrayAvailable { get; }

    IDesktop Desktop { get; }

    IGlobalHotkeys Hotkeys { get; }

    IKeyboardDevice CreateKeyboard(TypingMethod method, bool shiftEnterForNewLines);

    /// <summary>The physical layout of the user's keyboard, used to make realistic neighbouring-key typos.</summary>
    KeyboardGeometry GetKeyboardGeometry();

    /// <summary>Called while a typing session runs (Windows raises its timer resolution).</summary>
    IDisposable BeginTyping();

    /// <summary>Characters in <paramref name="text"/> that can't be typed on this computer right now.</summary>
    IReadOnlyCollection<string> FindUntypeableCharacters(string text);

    /// <summary>A limitation worth telling the user about, or null. Re-checked whenever AutoKey is activated.</summary>
    PlatformNotice? GetNotice();

    /// <summary>Explains why input to the target window is blocked (<see cref="PauseReason.TargetBlocked"/>).</summary>
    /// <param name="targetTitle">Title of the target window, if known.</param>
    /// <param name="resumeHint">How to resume, for example "press F8 or click Resume".</param>
    string DescribeBlockedTarget(string? targetTitle, string resumeHint);

    /// <summary>Lets the platform know about one of AutoKey's own windows.</summary>
    void TrackWindow(Window window);

    /// <summary>Stops the window from taking focus when clicked, where the platform allows it.</summary>
    void MakeNonActivating(Window window);
}

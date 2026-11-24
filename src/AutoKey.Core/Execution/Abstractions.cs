using AutoKey.Core.Planning;

namespace AutoKey.Core.Execution;

/// <summary>A key or character ready to be pressed, as resolved by an <see cref="IKeyboardDevice"/>.</summary>
/// <param name="VirtualKey">Virtual-key code of a physical key, or 0 when <paramref name="Text"/> is sent instead.</param>
/// <param name="ScanCode">Hardware scan code that matches <paramref name="VirtualKey"/>.</param>
/// <param name="Text">Characters sent directly as Unicode input instead of a physical key; null for physical keys.</param>
/// <param name="NeedsShift">Whether Shift must be held while the key is pressed.</param>
public readonly record struct KeyChord(ushort VirtualKey, ushort ScanCode, string? Text, bool NeedsShift);

/// <summary>Sends key events to the operating system.</summary>
/// <remarks>All members are called from the typing thread.</remarks>
public interface IKeyboardDevice
{
    /// <summary>Works out which key (and Shift state) produces a keystroke on the current keyboard layout.</summary>
    KeyChord Resolve(in Keystroke keystroke);

    /// <summary>Presses the key down (without touching Shift).</summary>
    void Press(in KeyChord chord);

    /// <summary>Releases a key pressed with <see cref="Press"/>; does nothing if it isn't held.</summary>
    void Release(in KeyChord chord);

    /// <summary>Presses or releases Shift.</summary>
    void SetShift(bool pressed);

    /// <summary>Releases every key this device is holding, including Shift.</summary>
    void ReleaseAll();

    /// <summary>Whether the user is physically holding Ctrl, Alt or the Windows key.</summary>
    bool IsUserHoldingModifiers();
}

/// <summary>Information about the desktop's windows.</summary>
public interface IDesktop
{
    /// <summary>The window that currently receives keyboard input, or 0.</summary>
    nint GetForegroundWindow();

    /// <summary>Whether the window belongs to this application.</summary>
    bool IsOwnWindow(nint window);

    string GetWindowTitle(nint window);

    /// <summary>
    /// False when Windows would silently block simulated input to the window, e.g. because it runs as
    /// administrator and this application doesn't.
    /// </summary>
    bool CanSendInputTo(nint window);

    /// <summary>
    /// Whether the window is a code editor that changes what is typed into it (autocomplete, auto-indent,
    /// auto-closing brackets), so that <see cref="TypingSession"/> has to counter it.
    /// </summary>
    bool IsCodeEditor(nint window) => false;
}

using AutoKey.Core.Execution;
using AutoKey.Core.Planning;
using SharpHook.Data;
using SharpHook.Simulation;

namespace AutoKey.Platform;

/// <summary>
/// Sends keystrokes through SharpHook (libuiohook): Quartz events on macOS, XTest on X11, or a virtual uinput
/// keyboard on Wayland.
/// </summary>
internal sealed class HookKeyboardDevice : IKeyboardDevice
{
    private readonly IEventSimulator _simulator;
    private readonly bool _shiftEnterForNewLines;
    private readonly bool _canTypeText;
    private readonly Func<char, (KeyCode Key, bool Shift)?>? _findKey;
    private readonly Func<bool> _userHoldsModifiers;
    private readonly List<KeyChord> _held = [];
    private bool _shiftDown;

    /// <param name="simulator">The SharpHook simulator.</param>
    /// <param name="shiftEnterForNewLines">Type new lines with Shift+Enter.</param>
    /// <param name="canTypeText">
    /// Whether the backend can type arbitrary Unicode text (not on Wayland). Used for characters that
    /// <paramref name="findKey"/> can't find on the keyboard.
    /// </param>
    /// <param name="findKey">
    /// Finds the key (and Shift state) that produces a character on the current layout, or null to type everything as text.
    /// </param>
    /// <param name="userHoldsModifiers">Whether the user is physically holding Ctrl, Alt, or Command/Super.</param>
    public HookKeyboardDevice(
        IEventSimulator simulator,
        bool shiftEnterForNewLines,
        bool canTypeText,
        Func<char, (KeyCode Key, bool Shift)?>? findKey,
        Func<bool> userHoldsModifiers)
    {
        _simulator = simulator;
        _shiftEnterForNewLines = shiftEnterForNewLines;
        _canTypeText = canTypeText;
        _findKey = findKey;
        _userHoldsModifiers = userHoldsModifiers;
    }

    public KeyChord Resolve(in Keystroke keystroke)
    {
        switch (keystroke.Kind)
        {
            case KeystrokeKind.Backspace:
                return Key(KeyCode.VcBackspace, shift: false);
            case KeystrokeKind.Tab:
                return Key(KeyCode.VcTab, shift: false);
            case KeystrokeKind.Enter:
                return Key(KeyCode.VcEnter, _shiftEnterForNewLines);
            case KeystrokeKind.Left:
                return Key(KeyCode.VcLeft, shift: false);
            case KeystrokeKind.Right:
                return Key(KeyCode.VcRight, shift: false);
            case KeystrokeKind.Home:
                return Key(KeyCode.VcHome, shift: false);
            case KeystrokeKind.Delete:
                return Key(KeyCode.VcDelete, shift: false);
        }

        // Press the real key where the layout has the character (like a person, and more reliable than X11's
        // text emulation); send everything else as text.
        string text = keystroke.Character.ToString();
        if (text.Length == 1 && _findKey?.Invoke(text[0]) is { } key)
        {
            return Key(key.Key, key.Shift);
        }

        if (_canTypeText)
        {
            return new KeyChord(0, 0, text, NeedsShift: false);
        }

        throw new InvalidOperationException($"“{text}” can't be typed with the current keyboard layout.");
    }

    public void Press(in KeyChord chord)
    {
        if (chord.Text is string text)
        {
            // Text is typed in one go (press and release), so nothing stays held.
            Check(_simulator.SimulateTextEntry(text), "type text");
            return;
        }

        Check(_simulator.SimulateKeyPress((KeyCode)chord.VirtualKey), "press a key");
        _held.Add(chord);
    }

    public void Release(in KeyChord chord)
    {
        int index = _held.IndexOf(chord);
        if (index < 0)
        {
            return;
        }

        _held.RemoveAt(index);
        _simulator.SimulateKeyRelease((KeyCode)chord.VirtualKey);
    }

    public void SetShift(bool pressed)
    {
        if (pressed == _shiftDown)
        {
            return;
        }

        Check(pressed ? _simulator.SimulateKeyPress(KeyCode.VcLeftShift) : _simulator.SimulateKeyRelease(KeyCode.VcLeftShift), "press Shift");
        _shiftDown = pressed;
    }

    public void ReleaseAll()
    {
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            _simulator.SimulateKeyRelease((KeyCode)_held[i].VirtualKey);
        }

        _held.Clear();
        if (_shiftDown)
        {
            _simulator.SimulateKeyRelease(KeyCode.VcLeftShift);
            _shiftDown = false;
        }
    }

    public bool IsUserHoldingModifiers() => _userHoldsModifiers();

    private static KeyChord Key(KeyCode key, bool shift) => new((ushort)key, 0, null, shift);

    private static void Check(UioHookResult result, string action)
    {
        if (result != UioHookResult.Success)
        {
            throw new InvalidOperationException($"Couldn't {action} ({result}).");
        }
    }
}

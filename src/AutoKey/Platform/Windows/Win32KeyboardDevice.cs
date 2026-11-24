using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AutoKey.Core.Execution;
using AutoKey.Core.Planning;
using static AutoKey.Platform.Windows.NativeMethods;

namespace AutoKey.Platform.Windows;

/// <summary>Sends keystrokes with the Win32 <c>SendInput</c> API.</summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32KeyboardDevice(TypingMethod method, bool shiftEnterForNewLines) : IKeyboardDevice
{
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    private readonly Dictionary<(nint Layout, char Character, bool CapsLock), (ushort Vk, bool Shift)?> _cache = [];
    private readonly List<KeyChord> _held = [];
    private readonly byte[] _keyState = new byte[256];
    private readonly char[] _buffer = new char[8];
    private bool _shiftDown;

    public KeyChord Resolve(in Keystroke keystroke)
    {
        nint layout = ForegroundLayout();
        switch (keystroke.Kind)
        {
            case KeystrokeKind.Backspace:
                return Physical(VK_BACK, needsShift: false, layout);
            case KeystrokeKind.Tab:
                return Physical(VK_TAB, needsShift: false, layout);
            case KeystrokeKind.Enter:
                return Physical(VK_RETURN, shiftEnterForNewLines, layout);
            case KeystrokeKind.Left:
                return Physical(VK_LEFT, needsShift: false, layout);
            case KeystrokeKind.Right:
                return Physical(VK_RIGHT, needsShift: false, layout);
            case KeystrokeKind.Home:
                return Physical(VK_HOME, needsShift: false, layout);
            case KeystrokeKind.Delete:
                return Physical(VK_DELETE, needsShift: false, layout);
        }

        string text = keystroke.Character.ToString();
        if (method == TypingMethod.Keyboard && text.Length == 1 && ResolveKey(text[0], layout) is { } key)
        {
            return Physical(key.Vk, key.Shift, layout);
        }

        return new KeyChord(0, 0, text, NeedsShift: false);
    }

    public void Press(in KeyChord chord)
    {
        if (chord.Text is string text)
        {
            // Characters outside the Basic Multilingual Plane arrive as two UTF-16 units: tap all but the last,
            // which stays down until Release, like a normal key.
            var inputs = new INPUT[(text.Length * 2) - 1];
            int n = 0;
            for (int i = 0; i < text.Length; i++)
            {
                inputs[n++] = UnicodeInput(text[i], keyUp: false);
                if (i < text.Length - 1)
                {
                    inputs[n++] = UnicodeInput(text[i], keyUp: true);
                }
            }

            Send(inputs);
        }
        else
        {
            Send(KeyInput(chord.VirtualKey, chord.ScanCode, keyUp: false));
        }

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
        SendKeyUp(chord);
    }

    public void SetShift(bool pressed)
    {
        if (pressed == _shiftDown)
        {
            return;
        }

        Send(KeyInput(VK_LSHIFT, SCAN_LSHIFT, keyUp: !pressed));
        _shiftDown = pressed;
    }

    public void ReleaseAll()
    {
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            SendKeyUp(_held[i]);
        }

        _held.Clear();
        SetShift(false);
    }

    public bool IsUserHoldingModifiers() =>
        IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN);

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>The keyboard layout of the window that will receive the input.</summary>
    private static nint ForegroundLayout()
    {
        nint window = GetForegroundWindow();
        uint thread = window == 0 ? 0 : GetWindowThreadProcessId(window, out _);
        return GetKeyboardLayout(thread);
    }

    private static KeyChord Physical(ushort virtualKey, bool needsShift, nint layout) =>
        new(virtualKey, (ushort)MapVirtualKeyExW(virtualKey, MAPVK_VK_TO_VSC, layout), null, needsShift);

    /// <summary>
    /// Finds the key and Shift state that produce <paramref name="c"/> on the layout, taking Caps Lock into
    /// account. Returns null for characters that need AltGr or a dead key, or aren't on the layout at all;
    /// those are sent as Unicode instead.
    /// </summary>
    private (ushort Vk, bool Shift)? ResolveKey(char c, nint layout)
    {
        bool capsLock = (GetKeyState(VK_CAPITAL) & 1) != 0;
        var cacheKey = (layout, c, capsLock);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        (ushort, bool)? result = null;
        short scan = VkKeyScanExW(c, layout);
        if (scan != -1)
        {
            ushort vk = (ushort)(scan & 0xFF);
            int modifiers = (scan >> 8) & 0xFF;
            bool needsCtrlOrAlt = (modifiers & 0b110) != 0;
            if (!needsCtrlOrAlt)
            {
                bool preferShift = (modifiers & 1) != 0;
                if (Produces(vk, preferShift, capsLock, layout, c))
                {
                    result = (vk, preferShift);
                }
                else if (Produces(vk, !preferShift, capsLock, layout, c))
                {
                    result = (vk, !preferShift);
                }
            }
        }

        _cache[cacheKey] = result;
        return result;
    }

    /// <summary>Asks the layout what the key types in the given Shift and Caps Lock state.</summary>
    private bool Produces(ushort vk, bool shift, bool capsLock, nint layout, char expected)
    {
        Array.Clear(_keyState);
        if (shift)
        {
            _keyState[VK_SHIFT] = 0x80;
            _keyState[VK_LSHIFT] = 0x80;
        }

        if (capsLock)
        {
            _keyState[VK_CAPITAL] = 0x01;
        }

        uint scanCode = MapVirtualKeyExW(vk, MAPVK_VK_TO_VSC, layout);
        int count = ToUnicodeEx(vk, scanCode, _keyState, _buffer, _buffer.Length, TOUNICODE_NO_STATE_CHANGE, layout);
        return count == 1 && _buffer[0] == expected; // negative means dead key
    }

    private static void SendKeyUp(KeyChord chord)
    {
        if (chord.Text is string text)
        {
            Send(UnicodeInput(text[^1], keyUp: true));
        }
        else
        {
            Send(KeyInput(chord.VirtualKey, chord.ScanCode, keyUp: true));
        }
    }

    private static INPUT KeyInput(ushort virtualKey, ushort scanCode, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = scanCode,
                dwFlags = (keyUp ? KEYEVENTF_KEYUP : 0) | (IsExtendedKey(virtualKey) ? KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };

    /// <summary>The arrow, Home and Delete keys share scan codes with the numeric keypad; this flag tells them apart.</summary>
    private static bool IsExtendedKey(ushort virtualKey) => virtualKey is VK_LEFT or VK_RIGHT or VK_HOME or VK_DELETE;

    private static INPUT UnicodeInput(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0) },
        },
    };

    // When Windows blocks the input (UIPI), SendInput fails silently; the session detects that case up front.
    private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, InputSize);
}

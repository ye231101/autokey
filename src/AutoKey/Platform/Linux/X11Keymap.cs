using System.Runtime.Versioning;
using System.Text;
using AutoKey.Core.Keyboard;
using SharpHook.Data;
using static AutoKey.Platform.Linux.X11Native;

namespace AutoKey.Platform.Linux;

/// <summary>
/// The characters on each key of the user's current keyboard layout, read from the X server (under Wayland,
/// XWayland mirrors the compositor's layout). Used to type with key presses where text can't be sent directly,
/// and to model the keyboard for realistic typos.
/// </summary>
internal sealed class X11Keymap
{
    private readonly Dictionary<char, (KeyCode Key, bool Shift)> _keys = [];
    private readonly bool _capsLock;

    private X11Keymap(IReadOnlyDictionary<uint, (char Normal, char Shifted)> characters, bool capsLock)
    {
        _capsLock = capsLock;

        // Prefer reaching a character without Shift when both are possible.
        foreach (var (scan, key) in PhysicalKeys.KeyAtScanCode)
        {
            Add(characters.GetValueOrDefault(scan).Normal, key, shift: false);
        }

        foreach (var (scan, key) in PhysicalKeys.KeyAtScanCode)
        {
            Add(characters.GetValueOrDefault(scan).Shifted, key, shift: true);
        }

        var normalRows = new string[PhysicalKeys.ScanCodeRows.Length];
        var shiftedRows = new string[PhysicalKeys.ScanCodeRows.Length];
        int letters = 0;
        for (int row = 0; row < PhysicalKeys.ScanCodeRows.Length; row++)
        {
            uint[] scans = PhysicalKeys.ScanCodeRows[row];
            normalRows[row] = new string(scans.Select(s => characters.GetValueOrDefault(s).Normal).ToArray());
            shiftedRows[row] = new string(scans.Select(s => characters.GetValueOrDefault(s).Shifted).ToArray());
            letters += normalRows[row].Count(char.IsLetter);
        }

        Geometry = letters >= 15 ? KeyboardGeometry.FromRows(normalRows, shiftedRows) : KeyboardGeometry.UsQwerty;
    }

    public KeyboardGeometry Geometry { get; }

    /// <summary>The US QWERTY layout, used when the X server can't be asked.</summary>
    public static X11Keymap UsQwerty { get; } = CreateUsQwerty();

    /// <summary>Reads the current layout (and Caps Lock state) from the X server, or falls back to US QWERTY.</summary>
    public static X11Keymap Load() => TryLoad() ?? UsQwerty;

    /// <summary>The key, and whether Shift is needed, to type <paramref name="c"/>; null if the layout lacks it.</summary>
    public (KeyCode Key, bool Shift)? Find(char c)
    {
        if (!_keys.TryGetValue(c, out var key))
        {
            return null;
        }

        // With Caps Lock on, letter keys produce the opposite case.
        return _capsLock && char.IsLetter(c) ? (key.Key, !key.Shift) : key;
    }

    /// <summary>The distinct characters of <paramref name="text"/> this layout can't type, in order of appearance.</summary>
    public IReadOnlyCollection<string> FindMissing(string text)
    {
        var seen = new HashSet<Rune>();
        var missing = new List<string>();
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (rune.Value is not ('\n' or '\t') && seen.Add(rune) && (!rune.IsBmp || Find((char)rune.Value) is null))
            {
                missing.Add(rune.ToString());
            }
        }

        return missing;
    }

    private void Add(char c, KeyCode key, bool shift)
    {
        if (c != '\0')
        {
            _keys.TryAdd(c, (key, shift));
        }
    }

    /// <summary>Reads the current layout (and Caps Lock state) from the X server; null if that isn't possible.</summary>
    public static X11Keymap? TryLoad()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return null;
        }

        nint display = 0;
        try
        {
            display = XOpenDisplay(0);
            if (display == 0)
            {
                return null;
            }

            X11Errors.Ignore(display);
            var state = new byte[32];
            int group = 0;
            bool capsLock = false;
            if (XkbGetState(display, XkbUseCoreKbd, state) == Success)
            {
                group = state[0];
                capsLock = (state[9] & 0x02) != 0; // LockMask in the locked modifiers
            }

            var characters = new Dictionary<uint, (char, char)>();
            foreach (uint scan in PhysicalKeys.KeyAtScanCode.Keys)
            {
                uint keycode = scan + 8; // X11 key codes are evdev codes plus 8
                characters[scan] = (
                    KeysymToChar(XkbKeycodeToKeysym(display, keycode, group, 0)),
                    KeysymToChar(XkbKeycodeToKeysym(display, keycode, group, 1)));
            }

            return new X11Keymap(characters, capsLock);
        }
        catch (Exception)
        {
            return null; // libX11 missing or unusable
        }
        finally
        {
            if (display != 0)
            {
                XCloseDisplay(display);
                X11Errors.Forget(display);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static char KeysymToChar(nuint keysym)
    {
        // Latin-1 keysyms equal their code points; 0x01xxxxxx keysyms carry a Unicode code point directly.
        if (keysym is >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF)
        {
            return (char)keysym;
        }

        if ((keysym & 0xFF000000) == 0x01000000)
        {
            nuint codePoint = keysym & 0x00FFFFFF;
            return codePoint is >= 0x20 and <= 0xFFFF && !char.IsSurrogate((char)codePoint) ? (char)codePoint : '\0';
        }

        if (keysym is 0 or > 0xFFFF)
        {
            return '\0';
        }

        uint utf32 = XkbCommon.KeysymToUtf32((uint)keysym); // legacy keysyms (Cyrillic, Greek, …)
        return utf32 is >= 0x20 and <= 0xFFFF && !char.IsSurrogate((char)utf32) ? (char)utf32 : '\0';
    }

    private static X11Keymap CreateUsQwerty()
    {
        var characters = new Dictionary<uint, (char, char)> { [PhysicalKeys.SpaceScanCode] = (' ', ' ') };
        for (int row = 0; row < PhysicalKeys.ScanCodeRows.Length; row++)
        {
            for (int j = 0; j < PhysicalKeys.ScanCodeRows[row].Length; j++)
            {
                characters[PhysicalKeys.ScanCodeRows[row][j]] = (PhysicalKeys.UsNormalRows[row][j], PhysicalKeys.UsShiftedRows[row][j]);
            }
        }

        return new X11Keymap(characters, capsLock: false);
    }
}

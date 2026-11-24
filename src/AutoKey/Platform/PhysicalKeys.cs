using SharpHook.Data;

namespace AutoKey.Platform;

/// <summary>
/// The main block of a PC keyboard, identified by scan codes. Scan codes describe physical positions regardless of
/// the layout; on Linux they also equal the evdev key codes (X11 key codes are 8 higher).
/// </summary>
internal static class PhysicalKeys
{
    /// <summary>Rows of the main block, from the number row down, as used by <see cref="AutoKey.Core.Keyboard.KeyboardGeometry"/>.</summary>
    public static readonly uint[][] ScanCodeRows =
    [
        [0x29, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D], // ` 1 … =
        [0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x2B], // Q … ] \
        [0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28],             // A … '
        [0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35],                   // Z … /
    ];

    public const uint SpaceScanCode = 0x39;

    /// <summary>The SharpHook key that sits at each scan code of the main block.</summary>
    public static readonly IReadOnlyDictionary<uint, KeyCode> KeyAtScanCode = new Dictionary<uint, KeyCode>
    {
        [0x29] = KeyCode.VcBackQuote, [0x02] = KeyCode.Vc1, [0x03] = KeyCode.Vc2, [0x04] = KeyCode.Vc3,
        [0x05] = KeyCode.Vc4, [0x06] = KeyCode.Vc5, [0x07] = KeyCode.Vc6, [0x08] = KeyCode.Vc7, [0x09] = KeyCode.Vc8,
        [0x0A] = KeyCode.Vc9, [0x0B] = KeyCode.Vc0, [0x0C] = KeyCode.VcMinus, [0x0D] = KeyCode.VcEquals,
        [0x10] = KeyCode.VcQ, [0x11] = KeyCode.VcW, [0x12] = KeyCode.VcE, [0x13] = KeyCode.VcR, [0x14] = KeyCode.VcT,
        [0x15] = KeyCode.VcY, [0x16] = KeyCode.VcU, [0x17] = KeyCode.VcI, [0x18] = KeyCode.VcO, [0x19] = KeyCode.VcP,
        [0x1A] = KeyCode.VcOpenBracket, [0x1B] = KeyCode.VcCloseBracket, [0x2B] = KeyCode.VcBackslash,
        [0x1E] = KeyCode.VcA, [0x1F] = KeyCode.VcS, [0x20] = KeyCode.VcD, [0x21] = KeyCode.VcF, [0x22] = KeyCode.VcG,
        [0x23] = KeyCode.VcH, [0x24] = KeyCode.VcJ, [0x25] = KeyCode.VcK, [0x26] = KeyCode.VcL,
        [0x27] = KeyCode.VcSemicolon, [0x28] = KeyCode.VcQuote,
        [0x2C] = KeyCode.VcZ, [0x2D] = KeyCode.VcX, [0x2E] = KeyCode.VcC, [0x2F] = KeyCode.VcV, [0x30] = KeyCode.VcB,
        [0x31] = KeyCode.VcN, [0x32] = KeyCode.VcM, [0x33] = KeyCode.VcComma, [0x34] = KeyCode.VcPeriod,
        [0x35] = KeyCode.VcSlash, [SpaceScanCode] = KeyCode.VcSpace,
    };

    /// <summary>Characters of the US QWERTY layout, row by row, matching <see cref="ScanCodeRows"/>.</summary>
    public static readonly string[] UsNormalRows = ["`1234567890-=", "qwertyuiop[]\\", "asdfghjkl;'", "zxcvbnm,./"];

    public static readonly string[] UsShiftedRows = ["~!@#$%^&*()_+", "QWERTYUIOP{}|", "ASDFGHJKL:\"", "ZXCVBNM<>?"];
}

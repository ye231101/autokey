using System.Runtime.Versioning;
using AutoKey.Core.Keyboard;
using static AutoKey.Platform.PhysicalKeys;
using static AutoKey.Platform.Windows.NativeMethods;

namespace AutoKey.Platform.Windows;

/// <summary>Builds a <see cref="KeyboardGeometry"/> from the user's active keyboard layout (QWERTZ, AZERTY, Cyrillic, …).</summary>
[SupportedOSPlatform("windows")]
internal static class SystemKeyboardGeometry
{
    public static KeyboardGeometry ForCurrentLayout()
    {
        try
        {
            return Create(GetKeyboardLayout(0));
        }
        catch (Exception)
        {
            return KeyboardGeometry.UsQwerty;
        }
    }

    private static KeyboardGeometry Create(nint layout)
    {
        var state = new byte[256];
        var buffer = new char[8];
        var normalRows = new string[ScanCodeRows.Length];
        var shiftedRows = new string[ScanCodeRows.Length];
        int letters = 0;

        for (int row = 0; row < ScanCodeRows.Length; row++)
        {
            uint[] scanCodes = ScanCodeRows[row];
            var normal = new char[scanCodes.Length];
            var shifted = new char[scanCodes.Length];
            for (int j = 0; j < scanCodes.Length; j++)
            {
                uint vk = MapVirtualKeyExW(scanCodes[j], MAPVK_VSC_TO_VK, layout);
                normal[j] = vk == 0 ? '\0' : CharacterFor(vk, scanCodes[j], shift: false);
                shifted[j] = vk == 0 ? '\0' : CharacterFor(vk, scanCodes[j], shift: true);
                if (char.IsLetter(normal[j]))
                {
                    letters++;
                }
            }

            normalRows[row] = new string(normal);
            shiftedRows[row] = new string(shifted);
        }

        // Layouts without a usable letter block (some IMEs) fall back to QWERTY.
        return letters >= 15 ? KeyboardGeometry.FromRows(normalRows, shiftedRows) : KeyboardGeometry.UsQwerty;

        char CharacterFor(uint vk, uint scanCode, bool shift)
        {
            Array.Clear(state);
            if (shift)
            {
                state[VK_SHIFT] = 0x80;
                state[VK_LSHIFT] = 0x80;
            }

            int count = ToUnicodeEx(vk, scanCode, state, buffer, buffer.Length, TOUNICODE_NO_STATE_CHANGE, layout);
            return count == 1 && !char.IsControl(buffer[0]) ? buffer[0] : '\0';
        }
    }
}

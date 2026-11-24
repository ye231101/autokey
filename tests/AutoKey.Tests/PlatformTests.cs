using System.Text;
using AutoKey.Platform;
using AutoKey.Platform.Linux;
using SharpHook.Data;

namespace AutoKey.Tests;

/// <summary>The key tables that macOS/Linux typing relies on, checked on any OS.</summary>
public class PhysicalKeysTests
{
    [Fact]
    public void Every_key_in_the_rows_has_a_key_code()
    {
        foreach (uint scan in PhysicalKeys.ScanCodeRows.SelectMany(row => row))
        {
            Assert.True(PhysicalKeys.KeyAtScanCode.ContainsKey(scan), $"Scan code 0x{scan:X2} has no key code.");
        }
    }

    [Fact]
    public void Key_codes_are_unique()
    {
        Assert.Equal(PhysicalKeys.KeyAtScanCode.Count, PhysicalKeys.KeyAtScanCode.Values.Distinct().Count());
    }

    [Fact]
    public void The_us_layout_lines_up_with_the_rows()
    {
        for (int row = 0; row < PhysicalKeys.ScanCodeRows.Length; row++)
        {
            Assert.Equal(PhysicalKeys.ScanCodeRows[row].Length, PhysicalKeys.UsNormalRows[row].Length);
            Assert.Equal(PhysicalKeys.ScanCodeRows[row].Length, PhysicalKeys.UsShiftedRows[row].Length);
        }
    }
}

public class X11KeymapTests
{
    [Theory]
    [InlineData('a', KeyCode.VcA, false)]
    [InlineData('A', KeyCode.VcA, true)]
    [InlineData('q', KeyCode.VcQ, false)]
    [InlineData('z', KeyCode.VcZ, false)]
    [InlineData('1', KeyCode.Vc1, false)]
    [InlineData('!', KeyCode.Vc1, true)]
    [InlineData('0', KeyCode.Vc0, false)]
    [InlineData(')', KeyCode.Vc0, true)]
    [InlineData('-', KeyCode.VcMinus, false)]
    [InlineData('+', KeyCode.VcEquals, true)]
    [InlineData('[', KeyCode.VcOpenBracket, false)]
    [InlineData('|', KeyCode.VcBackslash, true)]
    [InlineData(';', KeyCode.VcSemicolon, false)]
    [InlineData('"', KeyCode.VcQuote, true)]
    [InlineData('<', KeyCode.VcComma, true)]
    [InlineData('?', KeyCode.VcSlash, true)]
    [InlineData('`', KeyCode.VcBackQuote, false)]
    [InlineData('~', KeyCode.VcBackQuote, true)]
    [InlineData(' ', KeyCode.VcSpace, false)]
    public void The_us_layout_finds_the_key_for_each_character(char c, KeyCode key, bool shift)
    {
        Assert.Equal((key, shift), X11Keymap.UsQwerty.Find(c));
    }

    [Theory]
    [InlineData('é')]
    [InlineData('€')]
    [InlineData('ж')]
    [InlineData('\n')]
    public void Characters_missing_from_the_layout_have_no_key(char c)
    {
        Assert.Null(X11Keymap.UsQwerty.Find(c));
    }

    [Fact]
    public void Missing_characters_are_listed_once_in_order_of_appearance()
    {
        Assert.Equal(["é", "ж", "👋"], X11Keymap.UsQwerty.FindMissing("aéb\nж\té👋 ж👋"));
        Assert.Empty(X11Keymap.UsQwerty.FindMissing("Plain text,\n\twith a tab."));
    }

    [Fact]
    public void Every_printable_ascii_character_can_be_typed_on_the_us_layout()
    {
        for (char c = ' '; c <= '~'; c++)
        {
            Assert.True(X11Keymap.UsQwerty.Find(c) is not null, $"'{c}' has no key.");
        }
    }

    [Fact]
    public void The_us_layout_also_describes_the_keyboard_for_typos()
    {
        string neighbors = string.Concat(X11Keymap.UsQwerty.Geometry.GetNeighbors(new Rune('f'))
            .Select(n => n.Rune.ToString()).Order(StringComparer.Ordinal));

        Assert.Equal("cdgrtv", neighbors);
    }

    [Fact]
    public void Reading_the_layout_without_an_x_server_gives_null()
    {
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 })
        {
            return; // a real X server is available here, so null wouldn't be expected
        }

        Assert.Null(X11Keymap.TryLoad());
        Assert.Same(X11Keymap.UsQwerty, X11Keymap.Load());
    }
}

public class HotkeyBindingTests
{
    private static readonly HookHotkeys.Binding F8 = new(KeyCode.VcF8, EventMask.None, "F8");
    private static readonly HookHotkeys.Binding ControlOptionP = new(KeyCode.VcP, EventMask.Ctrl | EventMask.Alt, "⌃⌥P");

    [Fact]
    public void A_plain_key_matches_alone_or_with_shift()
    {
        Assert.True(F8.Matches(KeyCode.VcF8, EventMask.None));
        Assert.True(F8.Matches(KeyCode.VcF8, EventMask.LeftShift));
        Assert.False(F8.Matches(KeyCode.VcF9, EventMask.None));
    }

    [Fact]
    public void A_plain_key_does_not_match_with_other_modifiers()
    {
        Assert.False(F8.Matches(KeyCode.VcF8, EventMask.LeftCtrl));
        Assert.False(F8.Matches(KeyCode.VcF8, EventMask.RightAlt));
        Assert.False(F8.Matches(KeyCode.VcF8, EventMask.LeftMeta));
    }

    [Fact]
    public void A_combination_needs_exactly_its_modifiers_on_either_side()
    {
        Assert.True(ControlOptionP.Matches(KeyCode.VcP, EventMask.LeftCtrl | EventMask.LeftAlt));
        Assert.True(ControlOptionP.Matches(KeyCode.VcP, EventMask.RightCtrl | EventMask.RightAlt));
        Assert.False(ControlOptionP.Matches(KeyCode.VcP, EventMask.LeftCtrl));
        Assert.False(ControlOptionP.Matches(KeyCode.VcP, EventMask.LeftCtrl | EventMask.LeftAlt | EventMask.LeftMeta));
        Assert.False(ControlOptionP.Matches(KeyCode.VcP, EventMask.None));
    }
}

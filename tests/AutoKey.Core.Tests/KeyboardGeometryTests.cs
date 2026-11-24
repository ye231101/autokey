using System.Text;
using AutoKey.Core.Keyboard;

namespace AutoKey.Core.Tests;

public class KeyboardGeometryTests
{
    private static readonly KeyboardGeometry Qwerty = KeyboardGeometry.UsQwerty;

    private static string Neighbors(KeyboardGeometry geometry, char c) =>
        string.Concat(geometry.GetNeighbors(new Rune(c)).Select(n => n.Rune.ToString()).Order(StringComparer.Ordinal));

    [Theory]
    [InlineData('f', "cdgrtv")]
    [InlineData('F', "CDGRTV")]
    [InlineData('q', "aw")]
    [InlineData('p', "lo")]
    [InlineData('m', "jkn")]
    [InlineData('5', "46")]
    [InlineData('1', "2")]
    public void Neighbors_are_adjacent_keys_of_the_same_kind(char c, string expected)
    {
        Assert.Equal(expected, Neighbors(Qwerty, c));
    }

    [Theory]
    [InlineData('é')]
    [InlineData(';')]
    [InlineData(' ')]
    [InlineData('!')]
    public void Characters_without_letter_or_digit_neighbours_have_none(char c)
    {
        Assert.False(Qwerty.HasNeighbors(new Rune(c)));
        Assert.Empty(Qwerty.GetNeighbors(new Rune(c)));
    }

    [Theory]
    [InlineData('a', Finger.LeftPinky, false)]
    [InlineData('f', Finger.LeftIndex, false)]
    [InlineData('j', Finger.RightIndex, false)]
    [InlineData('K', Finger.RightMiddle, true)]
    [InlineData(';', Finger.RightPinky, false)]
    [InlineData('!', Finger.LeftPinky, true)]
    [InlineData('6', Finger.RightIndex, false)]
    [InlineData(' ', Finger.Thumb, false)]
    public void Keys_are_assigned_to_touch_typing_fingers(char c, Finger finger, bool shifted)
    {
        Assert.True(Qwerty.TryGetKey(new Rune(c), out KeyPosition key, out bool isShifted));
        Assert.Equal(finger, key.Finger);
        Assert.Equal(shifted, isShifted);
    }

    [Fact]
    public void Unknown_characters_are_not_on_the_keyboard()
    {
        Assert.False(Qwerty.TryGetKey(new Rune('ж'), out _, out _));
    }

    [Fact]
    public void Layouts_can_be_described_by_rows()
    {
        // German QWERTZ: z and y swap places, umlauts sit at the right edge.
        var qwertz = KeyboardGeometry.FromRows(["^1234567890ß´", "qwertzuiopü+#", "asdfghjklöä", "yxcvbnm,.-"]);

        Assert.Equal("ghtu", Neighbors(qwertz, 'z'));
        Assert.Equal("ASX", Neighbors(qwertz, 'Y'));
        Assert.Contains(qwertz.GetNeighbors(new Rune('ö')), n => n.Rune.Value == 'ä');
        Assert.True(qwertz.TryGetKey(new Rune('Ü'), out _, out bool shifted) && shifted);
    }
}

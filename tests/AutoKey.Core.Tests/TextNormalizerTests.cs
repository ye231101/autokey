using AutoKey.Core.Text;

namespace AutoKey.Core.Tests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("a\r\nb\rc\nd", "a\nb\nc\nd")]
    [InlineData("tab\there", "tab\there")]
    [InlineData("bell\a null\0 bom﻿", "bell null bom")]
    [InlineData("pair 👋", "pair 👋")]
    public void Normalize_unifies_line_endings_and_drops_untypeable_characters(string? input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_drops_unpaired_surrogates()
    {
        // Built at run time: attribute arguments can't carry lone surrogates.
        string input = "lone " + '\uD83D' + " high, lone " + '\uDC4B' + " low, pair " + "👋";

        Assert.Equal("lone  high, lone  low, pair 👋", TextNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("one", 1)]
    [InlineData(" two  words ", 2)]
    [InlineData("line\nbreaks\tand tabs", 4)]
    public void CountWords_counts_whitespace_separated_words(string text, int expected)
    {
        Assert.Equal(expected, TextNormalizer.CountWords(text));
    }
}

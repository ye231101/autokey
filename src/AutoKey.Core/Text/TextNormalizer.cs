using System.Text;

namespace AutoKey.Core.Text;

/// <summary>
/// Prepares user text for typing: unifies line endings and drops characters that cannot be typed.
/// </summary>
public static class TextNormalizer
{
    /// <summary>
    /// Converts <c>\r\n</c> and lone <c>\r</c> to <c>\n</c>, and removes control characters (other than
    /// newline and tab), unpaired surrogates and byte-order marks.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                sb.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (c is '\n' or '\t')
            {
                sb.Append(c);
            }
            else if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(c).Append(text[i + 1]);
                    i++;
                }
            }
            else if (char.IsLowSurrogate(c) || char.IsControl(c) || c == '﻿')
            {
                // Unpaired surrogates, control characters and BOMs can't be typed meaningfully.
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Splits normalized text into Unicode scalar values (one per keystroke).</summary>
    public static Rune[] ToRunes(string normalized)
    {
        var runes = new Rune[normalized.Length];
        int count = 0;
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            runes[count++] = rune;
        }

        Array.Resize(ref runes, count); // only reallocates if the text has surrogate pairs
        return runes;
    }

    /// <summary>Counts whitespace-separated words.</summary>
    public static int CountWords(string text)
    {
        int words = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                words++;
            }
        }

        return words;
    }
}

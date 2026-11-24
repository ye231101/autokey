using System.Text;
using AutoKey.Core.Execution;
using AutoKey.Core.Planning;

namespace AutoKey.Core.Tests;

/// <summary>
/// A small model of how VS Code reacts to keys: autocomplete from the words in the document, auto-indent, comment
/// prefixes, auto-closing pairs that can be typed over, and lines that are re-indented when they start to match a
/// language's rule, like a closing bracket or <c>end</c> (even when the character is typed over a selection).
/// </summary>
internal sealed class FakeCodeEditor : IKeyboardDevice
{
    private const ushort Backspace = 0x08, Tab = 0x09, Enter = 0x0D, Home = 0x24, Left = 0x25, Right = 0x27, DeleteKey = 0x2E;
    private const string Openers = "([{\"'`";
    private const string Closers = ")]}\"'`";

    private readonly StringBuilder _text = new();
    private readonly HashSet<int> _autoClosed = []; // positions of closing characters the editor added
    private int _caret;
    private int _anchor;
    private bool _shift;
    private string? _suggestion;

    /// <summary>Called on the typing thread after each key press.</summary>
    public Action? AfterPress { get; set; }

    /// <summary>
    /// Uses YAML's rule, which only matches an indented closing brace, so typing the indentation can set it off.
    /// </summary>
    public bool IndentedBraceRule { get; init; }

    public string Text => _text.ToString();

    private bool HasSelection => _caret != _anchor;

    public KeyChord Resolve(in Keystroke keystroke) => keystroke.Kind switch
    {
        KeystrokeKind.Backspace => new KeyChord(Backspace, 0, null, false),
        KeystrokeKind.Enter => new KeyChord(Enter, 0, null, false),
        KeystrokeKind.Tab => new KeyChord(Tab, 0, null, false),
        KeystrokeKind.Left => new KeyChord(Left, 0, null, false),
        KeystrokeKind.Right => new KeyChord(Right, 0, null, false),
        KeystrokeKind.Home => new KeyChord(Home, 0, null, false),
        KeystrokeKind.Delete => new KeyChord(DeleteKey, 0, null, false),
        _ => new KeyChord(0, 0, keystroke.Character.ToString(), Rune.IsUpper(keystroke.Character)),
    };

    public void Press(in KeyChord chord)
    {
        switch (chord.VirtualKey)
        {
            case Backspace:
                DeleteLeft();
                break;
            case Enter when !AcceptSuggestion():
                NewLine();
                break;
            case Tab when !AcceptSuggestion():
                Replace(new string(' ', 4 - (ColumnOf(_caret) % 4)));
                break;
            case Left:
                MoveCaret(!_shift && HasSelection ? Math.Min(_caret, _anchor) : Step(_caret, -1));
                break;
            case Right:
                MoveCaret(!_shift && HasSelection ? Math.Max(_caret, _anchor) : Step(_caret, 1));
                break;
            case Home:
                MoveCaret(SmartHome());
                break;
            case DeleteKey:
                if (HasSelection)
                {
                    Replace(string.Empty);
                }
                else if (_caret < _text.Length)
                {
                    Delete(_caret, Step(_caret, 1) - _caret);
                }

                _suggestion = null;
                break;
            case 0:
                TypeText(chord.Text!);
                break;
        }

        AfterPress?.Invoke();
    }

    public void Release(in KeyChord chord)
    {
    }

    public void SetShift(bool pressed) => _shift = pressed;

    public void ReleaseAll() => _shift = false;

    public bool IsUserHoldingModifiers() => false;

    private void TypeText(string text)
    {
        char c = text[0];
        bool isWord = char.IsLetterOrDigit(c);
        if (!isWord)
        {
            AcceptSuggestion(); // punctuation commits the suggestion
        }

        int from = Math.Min(_caret, _anchor);
        int to = Math.Max(_caret, _anchor);
        int start = LineStart(from);
        string before = _text.ToString(start, from - start) + _text.ToString(to, LineEnd(to) - to);
        InsertTyped(text, c);
        start = LineStart(_caret);
        if (!MatchesOutdentRule(before) && MatchesOutdentRule(_text.ToString(start, LineEnd(_caret) - start)))
        {
            Reindent();
        }

        _suggestion = isWord ? FindSuggestion() : null;
    }

    private bool MatchesOutdentRule(string line)
    {
        string trimmed = line.TrimStart(' ', '\t');
        if (IndentedBraceRule)
        {
            return trimmed == "}" && trimmed.Length < line.Length;
        }

        return trimmed.StartsWith('}') || trimmed.StartsWith(']') || trimmed.StartsWith(')')
            || (trimmed.StartsWith("end", StringComparison.Ordinal) && (trimmed.Length == 3 || !char.IsLetterOrDigit(trimmed[3])))
            || trimmed.StartsWith("when ", StringComparison.Ordinal) // fires on the space after the word
            || (trimmed.StartsWith("case ", StringComparison.Ordinal) && trimmed.EndsWith(':')); // fires at the end of the line
    }

    /// <summary>Gives the current line the previous non-blank line's indentation, one level less.</summary>
    private void Reindent()
    {
        int start = LineStart(_caret);
        string indent = string.Empty;
        for (int line = start; line > 0;)
        {
            line = LineStart(line - 1);
            if (!IsBlank(line, LineEnd(line)))
            {
                string previous = LeadingWhitespace(line);
                indent = previous.Length >= 4 ? previous[..^4] : string.Empty;
                break;
            }
        }

        string current = LeadingWhitespace(start);
        int fromEnd = _text.Length - _caret;
        Delete(start, current.Length);
        Insert(start, indent);
        _caret = _anchor = _text.Length - fromEnd;
    }

    private void InsertTyped(string text, char c)
    {
        if (HasSelection)
        {
            bool blank = string.IsNullOrWhiteSpace(_text.ToString(Math.Min(_caret, _anchor), Math.Abs(_caret - _anchor)));
            int close = Openers.IndexOf(c);
            if (close >= 0 && !blank)
            {
                Replace(c + _text.ToString(Math.Min(_caret, _anchor), Math.Abs(_caret - _anchor)) + Closers[close]); // surround
            }
            else
            {
                Replace(text); // over a selection, a character is inserted as it is
            }
        }
        else if (_autoClosed.Contains(_caret) && _caret < _text.Length && _text[_caret] == c)
        {
            _autoClosed.Remove(_caret);
            MoveCaret(_caret + 1); // types over the closing character the editor added
        }
        else if (Openers.Contains(c) && ClosesHere(c))
        {
            Replace(text);
            Insert(_caret, Closers[Openers.IndexOf(c)].ToString());
            _autoClosed.Add(_caret);
        }
        else
        {
            Replace(text);
        }
    }

    private bool ClosesHere(char c)
    {
        bool beforeWord = _caret < _text.Length && char.IsLetterOrDigit(_text[_caret]);
        bool quoteAfterWord = c is '"' or '\'' && _caret > 0 && char.IsLetterOrDigit(_text[_caret - 1]);
        return !beforeWord && !quoteAfterWord;
    }

    private void NewLine()
    {
        int start = LineStart(_caret);
        string line = _text.ToString(start, _caret - start);
        string indent = LeadingWhitespace(start);
        string trimmed = line.Trim();
        string added = trimmed.StartsWith("/**", StringComparison.Ordinal) ? indent + " * "
            : trimmed.StartsWith('*') && !trimmed.EndsWith("*/", StringComparison.Ordinal) ? indent + "* "
            : trimmed.EndsWith('{') ? indent + "    "
            : indent;
        Replace("\n" + added);
    }

    private void DeleteLeft()
    {
        if (HasSelection)
        {
            Replace(string.Empty);
        }
        else if (_caret > 0)
        {
            int previous = Step(_caret, -1);
            int length = _autoClosed.Contains(_caret) ? _caret - previous + 1 : _caret - previous; // removes the pair
            Delete(previous, length);
            _caret = _anchor = previous;
        }

        _suggestion = null;
    }

    private bool AcceptSuggestion()
    {
        if (_suggestion is not { } suggestion)
        {
            return false;
        }

        _suggestion = null;
        int start = WordStart();
        Delete(start, _caret - start);
        _caret = _anchor = start;
        Replace(suggestion);
        return true;
    }

    private string? FindSuggestion()
    {
        int start = WordStart();
        string word = _text.ToString(start, _caret - start);
        for (int i = 0; i < _text.Length;)
        {
            if (!char.IsLetterOrDigit(_text[i]))
            {
                i++;
                continue;
            }

            int end = i;
            while (end < _text.Length && char.IsLetterOrDigit(_text[end]))
            {
                end++;
            }

            if (i != start && _text.ToString(i, end - i).StartsWith(word, StringComparison.Ordinal))
            {
                return _text.ToString(i, end - i);
            }

            i = end;
        }

        return null;
    }

    private void MoveCaret(int position)
    {
        _caret = Math.Clamp(position, 0, _text.Length);
        if (!_shift)
        {
            _anchor = _caret;
        }

        _suggestion = null; // moving the caret by selecting closes the list; the model closes it for any movement
    }

    private int LineEnd(int position)
    {
        while (position < _text.Length && _text[position] != '\n')
        {
            position++;
        }

        return position;
    }

    private int SmartHome()
    {
        int start = LineStart(_caret);
        int firstInk = start;
        int lineEnd = LineEnd(start);

        while (firstInk < lineEnd && _text[firstInk] is ' ' or '\t')
        {
            firstInk++;
        }

        if (firstInk == lineEnd)
        {
            return start; // a blank line has no first character to stop at
        }

        return _caret == firstInk ? start : firstInk;
    }

    private void Replace(string text)
    {
        int start = Math.Min(_caret, _anchor);
        Delete(start, Math.Abs(_caret - _anchor));
        Insert(start, text);
        _caret = _anchor = start + text.Length;
    }

    private void Insert(int position, string text)
    {
        _text.Insert(position, text);
        Shift(position, text.Length);
    }

    private void Delete(int position, int length)
    {
        _text.Remove(position, length);
        _autoClosed.RemoveWhere(p => p >= position && p < position + length);
        Shift(position + length, -length);
    }

    private void Shift(int from, int by)
    {
        int[] moved = _autoClosed.Where(p => p >= from).ToArray();
        _autoClosed.ExceptWith(moved);
        _autoClosed.UnionWith(moved.Select(p => p + by));
    }

    private int Step(int position, int direction)
    {
        int next = Math.Clamp(position + direction, 0, _text.Length);
        bool insidePair = next > 0 && next < _text.Length && char.IsLowSurrogate(_text[next]) && char.IsHighSurrogate(_text[next - 1]);
        return insidePair ? next + direction : next;
    }

    private int WordStart()
    {
        int start = _caret;
        while (start > 0 && char.IsLetterOrDigit(_text[start - 1]))
        {
            start--;
        }

        return start;
    }

    private int LineStart(int position)
    {
        while (position > 0 && _text[position - 1] != '\n')
        {
            position--;
        }

        return position;
    }

    private int ColumnOf(int position) => position - LineStart(position);

    private bool IsBlank(int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (_text[i] is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    private string LeadingWhitespace(int lineStart)
    {
        int end = lineStart;
        while (end < _text.Length && _text[end] is ' ' or '\t')
        {
            end++;
        }

        return _text.ToString(lineStart, end - lineStart);
    }
}

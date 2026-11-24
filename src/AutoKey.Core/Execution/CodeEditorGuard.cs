using System.Text;
using AutoKey.Core.Planning;

namespace AutoKey.Core.Execution;

/// <summary>An extra key press sent to a code editor.</summary>
/// <param name="Key">The key to press.</param>
/// <param name="Shift">Whether to hold Shift, which turns a caret movement into a selection.</param>
internal readonly record struct GuardKey(Keystroke Key, bool Shift = false);

/// <summary>
/// Works out the extra keys that stop a code editor from changing the text as it is typed.
/// </summary>
/// <remarks>
/// Editors like VS Code and Cursor react to what is typed, and none of it can be switched off from outside:
/// <list type="bullet">
/// <item>Enter, Tab and punctuation accept an open autocomplete suggestion instead of being typed;</item>
/// <item>a new line starts with the previous line's indentation, or with a comment prefix;</item>
/// <item>opening brackets, quotes and tags get a closing partner;</item>
/// <item>a line is re-indented when it turns out to be a closing bracket or start with a keyword like <c>end</c>.</item>
/// </list>
/// Ordinary keys avoid all of it, and leave the text and the caret exactly as if only the planned key was pressed:
/// <list type="bullet">
/// <item>Selecting a character closes the suggestions, and Right puts the caret back.</item>
/// <item>A key pressed over a selected space is inserted as it is, with no partner. The space is selected from the
/// left, because the editor only opens suggestions when the caret is at a word: with the caret to the right of the
/// space, a suggestion that was still on its way can't open at the last moment and swallow the key.</item>
/// <item>After Enter, selecting back to the start of the line catches whatever the editor added there.</item>
/// <item>At the end of a line, its indentation is selected and typed again in case the editor changed it. A
/// placeholder in front of the line meanwhile keeps the new indentation itself from setting off the editor's rules.</item>
/// </list>
/// </remarks>
internal sealed class CodeEditorGuard
{
    /// <summary>
    /// The deepest indentation that gets restored: shallower text is certainly on the first row of its line even with
    /// word wrap, where Home would otherwise stop at the start of a later row.
    /// </summary>
    private const int MaxRestoredIndent = 40;

    /// <summary>The longest line whose indentation gets restored, as the caret has to travel there and back.</summary>
    private const int MaxRestoredLine = 200;

    private static readonly GuardKey Space = Character(' ');
    private static readonly GuardKey Placeholder = Character('~');
    private static readonly GuardKey TabCharacter = Navigation(KeystrokeKind.Tab);
    private static readonly GuardKey Left = Navigation(KeystrokeKind.Left);
    private static readonly GuardKey Right = Navigation(KeystrokeKind.Right);
    private static readonly GuardKey Backspace = Navigation(KeystrokeKind.Backspace);
    private static readonly GuardKey Delete = Navigation(KeystrokeKind.Delete);
    private static readonly GuardKey SelectLeft = Left with { Shift = true };
    private static readonly GuardKey SelectRight = Right with { Shift = true };
    private static readonly GuardKey SelectToLineStart = Navigation(KeystrokeKind.Home) with { Shift = true };

    private readonly List<GuardKey> _keys = new(64);
    private readonly StringBuilder _line = new(); // what has been typed on the current line

    private bool _lineStartIsOurs;  // this session typed the line break, so nothing else is to the left on this line
    private int _typedLeftOfCaret;  // characters known to be left of the caret, so that Shift+Left has something to select

    /// <summary>The caret may be somewhere else now, for example after a pause.</summary>
    public void CaretMayHaveMoved()
    {
        _typedLeftOfCaret = 0;
        _lineStartIsOurs = false;
    }

    /// <summary>The keys that close an open list of suggestions without changing the text.</summary>
    public List<GuardKey> CloseSuggestions()
    {
        _keys.Clear();
        AddCloseSuggestions();
        return _keys;
    }

    /// <summary>The keys to press right before <paramref name="keystroke"/>.</summary>
    public List<GuardKey> BeforeKey(in Keystroke keystroke)
    {
        _keys.Clear();
        if (keystroke.Kind == KeystrokeKind.Backspace || IsWordCharacter(keystroke))
        {
            return _keys; // these neither accept a suggestion nor trigger anything else
        }

        AddCloseSuggestions();
        switch (keystroke.Kind)
        {
            case KeystrokeKind.Enter when IndentLength() < _line.Length:
                // The space comes first because typing it can itself set off a rule ("when " in Ruby).
                _keys.Add(Space);
                _keys.Add(Left);
                AddRestoreIndent();
                _keys.Add(SelectRight);
                break;

            case KeystrokeKind.Character when !IsSpaceOrTab(keystroke.Character):
                _keys.Add(Space);
                _keys.Add(Left);
                _keys.Add(SelectRight);
                break;
        }

        return _keys;
    }

    /// <summary>
    /// Records that <paramref name="keystroke"/> was pressed and returns the keys to press right after it.
    /// Called for every keystroke, also in other windows, so the guard knows the current line if focus moves here.
    /// </summary>
    public List<GuardKey> AfterKey(in Keystroke keystroke)
    {
        _keys.Clear();
        switch (keystroke.Kind)
        {
            case KeystrokeKind.Enter:
                _line.Clear();
                _lineStartIsOurs = true;
                _typedLeftOfCaret++;

                // Twice, because the first Home stops after a comment prefix's indentation (" * ").
                for (int round = 0; round < 2; round++)
                {
                    _keys.Add(SelectToLineStart);
                    _keys.Add(Space);
                    _keys.Add(Backspace);
                }

                break;

            case KeystrokeKind.Backspace:
                RemoveLastCharacter();
                _typedLeftOfCaret = Math.Max(0, _typedLeftOfCaret - 1);
                break;

            case KeystrokeKind.Tab:
                _line.Append('\t');
                _typedLeftOfCaret++;
                break;

            case KeystrokeKind.Character:
                _line.Append(keystroke.Character.ToString());
                _typedLeftOfCaret++;
                break;
        }

        return _keys;
    }

    /// <summary>The keys to press after the last keystroke, whose line no Enter is going to finish.</summary>
    public List<GuardKey> AtEndOfText()
    {
        _keys.Clear();
        AddRestoreIndent();
        return _keys;
    }

    private void AddCloseSuggestions()
    {
        if (_typedLeftOfCaret > 0)
        {
            _keys.Add(SelectLeft);
            _keys.Add(Right);
        }
    }

    /// <summary>
    /// From the end of the typed text, goes back to the line's first character, replaces whatever indentation is in
    /// front of it with the original one, and returns. Only for plain ASCII lines, where one press of Left is
    /// certainly one character.
    /// </summary>
    private void AddRestoreIndent()
    {
        int indent = IndentLength();
        if (!_lineStartIsOurs || indent == _line.Length || indent > MaxRestoredIndent
            || _line.Length > MaxRestoredLine || !IsAscii())
        {
            return;
        }

        int distance = _line.Length - indent;
        _keys.AddRange(Enumerable.Repeat(Left, distance));
        _keys.Add(SelectToLineStart);
        _keys.Add(Placeholder);
        _keys.Add(Left);
        for (int i = 0; i < indent; i++)
        {
            _keys.Add(_line[i] == '\t' ? TabCharacter : Space);
        }

        _keys.Add(Delete);
        _keys.AddRange(Enumerable.Repeat(Right, distance));
    }

    private int IndentLength()
    {
        int indent = 0;
        while (indent < _line.Length && _line[indent] is ' ' or '\t')
        {
            indent++;
        }

        return indent;
    }

    private bool IsAscii()
    {
        for (int i = 0; i < _line.Length; i++)
        {
            if (!char.IsAscii(_line[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void RemoveLastCharacter()
    {
        if (_line.Length >= 2 && char.IsLowSurrogate(_line[^1]) && char.IsHighSurrogate(_line[^2]))
        {
            _line.Length -= 2;
        }
        else if (_line.Length > 0)
        {
            _line.Length--;
        }
    }

    private static bool IsWordCharacter(in Keystroke keystroke) =>
        keystroke.Kind == KeystrokeKind.Character && Rune.IsLetterOrDigit(keystroke.Character);

    private static bool IsSpaceOrTab(Rune rune) => rune.Value is ' ' or '\t';

    private static GuardKey Character(char c) => new(new Keystroke(KeystrokeKind.Character, new Rune(c), 0, 0, false, 0));

    private static GuardKey Navigation(KeystrokeKind kind) => new(new Keystroke(kind, default, 0, 0, false, 0));
}

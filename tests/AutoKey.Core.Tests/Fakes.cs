using System.Collections.Concurrent;
using System.Text;
using AutoKey.Core.Execution;
using AutoKey.Core.Planning;

namespace AutoKey.Core.Tests;

/// <summary>A keyboard that "types" into a string buffer and checks that it is driven correctly.</summary>
internal sealed class FakeKeyboard : IKeyboardDevice
{
    private readonly object _lock = new();
    private readonly StringBuilder _output = new();
    private readonly List<KeyChord> _held = [];
    private int _presses;

    /// <summary>Called on the typing thread after each key press, with the number of presses so far.</summary>
    public Action<int>? AfterPress { get; set; }

    public volatile bool HoldingModifiers;

    public bool ShiftDown { get; private set; }

    public int ShiftViolations { get; private set; }

    public string Output
    {
        get
        {
            lock (_lock)
            {
                return _output.ToString();
            }
        }
    }

    public int HeldCount
    {
        get
        {
            lock (_lock)
            {
                return _held.Count;
            }
        }
    }

    public int Presses => Volatile.Read(ref _presses);

    public KeyChord Resolve(in Keystroke keystroke) => keystroke.Kind switch
    {
        KeystrokeKind.Backspace => new KeyChord(0x08, 0x0E, null, false),
        KeystrokeKind.Enter => new KeyChord(0x0D, 0x1C, null, false),
        KeystrokeKind.Tab => new KeyChord(0x09, 0x0F, null, false),
        _ => new KeyChord(0, 0, keystroke.Character.ToString(), Rune.IsUpper(keystroke.Character)),
    };

    public void Press(in KeyChord chord)
    {
        int presses;
        lock (_lock)
        {
            if (chord.NeedsShift != ShiftDown)
            {
                ShiftViolations++;
            }

            switch (chord.VirtualKey)
            {
                case 0x08 when _output.Length > 0:
                    bool pair = _output.Length >= 2 && char.IsLowSurrogate(_output[^1]);
                    _output.Length -= pair ? 2 : 1;
                    break;
                case 0x0D:
                    _output.Append('\n');
                    break;
                case 0x09:
                    _output.Append('\t');
                    break;
                case 0:
                    _output.Append(chord.Text);
                    break;
            }

            _held.Add(chord);
            presses = ++_presses;
        }

        AfterPress?.Invoke(presses);
    }

    public void Release(in KeyChord chord)
    {
        lock (_lock)
        {
            _held.Remove(chord);
        }
    }

    public void SetShift(bool pressed)
    {
        lock (_lock)
        {
            ShiftDown = pressed;
        }
    }

    public void ReleaseAll()
    {
        lock (_lock)
        {
            _held.Clear();
            ShiftDown = false;
        }
    }

    public bool IsUserHoldingModifiers() => HoldingModifiers;
}

internal sealed class FakeDesktop : IDesktop
{
    public const nint OwnWindow = 1;
    public const nint Editor = 100;

    private long _foreground = Editor;

    public nint Foreground
    {
        get => (nint)Interlocked.Read(ref _foreground);
        set => Interlocked.Exchange(ref _foreground, value);
    }

    public ConcurrentDictionary<nint, bool> Blocked { get; } = new();

    /// <summary>Whether windows count as code editors.</summary>
    public bool CodeEditor { get; init; }

    public bool IsCodeEditor(nint window) => CodeEditor;

    public nint GetForegroundWindow() => Foreground;

    public bool IsOwnWindow(nint window) => window == OwnWindow;

    public string GetWindowTitle(nint window) => $"Window {window}";

    public bool CanSendInputTo(nint window) => !Blocked.ContainsKey(window);
}

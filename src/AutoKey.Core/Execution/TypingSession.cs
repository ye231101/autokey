using System.Diagnostics;
using AutoKey.Core.Planning;

namespace AutoKey.Core.Execution;

/// <summary>
/// Types a <see cref="TypingPlan"/> into the foreground window on a background thread.
/// </summary>
/// <remarks>
/// The session counts down, waits until a window other than this application has focus, then plays the
/// keystrokes with their planned timing. It can be paused, resumed and stopped from any thread. Before each
/// keystroke it checks that the target window still has focus, so text never ends up in the wrong place,
/// and it releases every key it holds whenever it pauses or ends.
/// </remarks>
public sealed class TypingSession
{
    private readonly TypingPlan _plan;
    private readonly IKeyboardDevice _keyboard;
    private readonly IDesktop _desktop;
    private readonly SessionOptions _options;
    private readonly Random _random = new();
    private readonly Stopwatch _typingClock = new();

    // A plain object (not System.Threading.Lock) because Monitor.Wait/PulseAll are used as a condition variable.
    private readonly object _sync = new();

    private Thread? _thread;
    private bool _pauseRequested;
    private bool _stopRequested;
    private PauseReason _pauseReason;
    private SessionStatus _status = new(SessionState.NotStarted);

    // Only used by the typing thread.
    private nint _target;
    private string? _targetTitle;
    private bool _targetIsCodeEditor;
    private bool _shiftHeld;
    private int _charactersTyped;
    private readonly CodeEditorGuard _editorGuard = new();

    public TypingSession(TypingPlan plan, IKeyboardDevice keyboard, IDesktop desktop, SessionOptions? options = null)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _options = options ?? new SessionOptions();
    }

    /// <summary>Raised on the typing thread whenever the status changes.</summary>
    public event Action<SessionStatus>? StatusChanged;

    /// <summary>Raised on the typing thread after every keystroke.</summary>
    public event Action<SessionProgress>? ProgressChanged;

    public TypingPlan Plan => _plan;

    public SessionStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_thread is not null)
            {
                throw new InvalidOperationException("The session has already been started.");
            }

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "AutoKey typing",
                Priority = ThreadPriority.AboveNormal,
            };
        }

        _thread.Start();
    }

    public void Pause() => RequestPause(PauseReason.User);

    public void Resume()
    {
        lock (_sync)
        {
            _pauseRequested = false;
            Monitor.PulseAll(_sync);
        }
    }

    public void TogglePause()
    {
        lock (_sync)
        {
            _pauseRequested = !_pauseRequested;
            _pauseReason = PauseReason.User;
            Monitor.PulseAll(_sync);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _stopRequested = true;
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>Waits for the typing thread to finish.</summary>
    public bool WaitForExit(TimeSpan timeout) => _thread?.Join(timeout) ?? true;

    private void Run()
    {
        int next = 0;
        double countdownMs = Math.Max(0, _options.CountdownSeconds * 1000);
        bool countdownJustEnded = false;
        bool needTarget = true;
        bool shortenNextDelay = false;

        try
        {
            while (true)
            {
                bool stop, pause;
                PauseReason reason;
                lock (_sync)
                {
                    stop = _stopRequested;
                    pause = _pauseRequested;
                    reason = _pauseReason;
                }

                if (stop)
                {
                    ReleaseKeys();
                    Finish(SessionState.Stopped);
                    return;
                }

                if (pause)
                {
                    _typingClock.Stop();
                    CloseEditorSuggestions();
                    ReleaseKeys();
                    SetStatus(new SessionStatus(SessionState.Paused, reason, TargetTitle: _targetTitle));
                    if (!WaitWhilePaused())
                    {
                        Finish(SessionState.Stopped);
                        return;
                    }

                    // Focus may have moved while paused, so find the target again before typing on.
                    needTarget |= countdownMs <= 0;
                    countdownJustEnded = false;
                    shortenNextDelay = true;
                    continue;
                }

                if (countdownMs > 0)
                {
                    double seconds = Math.Ceiling(countdownMs / 1000);
                    SetStatus(new SessionStatus(SessionState.Countdown, CountdownSeconds: (int)seconds));
                    Sleep(countdownMs - ((seconds - 1) * 1000), out double slept); // until the number changes
                    countdownMs -= slept;
                    countdownJustEnded = countdownMs <= 0;
                    continue;
                }

                if (needTarget)
                {
                    bool acquired = AcquireTarget(skipGrace: countdownJustEnded, ref countdownMs);
                    countdownJustEnded = false;
                    if (!acquired)
                    {
                        continue;
                    }

                    needTarget = false;
                    SetStatus(new SessionStatus(SessionState.Typing, TargetTitle: _targetTitle));
                    _typingClock.Start();
                    continue;
                }

                if (next >= _plan.Count)
                {
                    FinishEditorLine();
                    ReleaseKeys();
                    Finish(SessionState.Completed);
                    return;
                }

                Keystroke keystroke = _plan[next];
                if (TypeKeystroke(keystroke, shortenNextDelay))
                {
                    _charactersTyped = keystroke.Progress;
                    next++;
                    shortenNextDelay = false;
                    ProgressChanged?.Invoke(new SessionProgress(
                        _charactersTyped,
                        _plan.CharacterCount,
                        next,
                        _typingClock.Elapsed,
                        _plan.RemainingFrom(next),
                        _plan.EstimatedDuration));
                }
            }
        }
        catch (Exception ex)
        {
            ReleaseKeys();
            Finish(SessionState.Faulted, ex.Message);
        }
    }

    /// <summary>
    /// Makes sure a suitable window has focus and remembers it as the target.
    /// Returns false when the caller should loop again (paused, stopped, countdown set, or focus still moving).
    /// </summary>
    private bool AcquireTarget(bool skipGrace, ref double countdownMs)
    {
        if (!WaitForModifierRelease())
        {
            return false;
        }

        nint window = _desktop.GetForegroundWindow();
        if (!IsCandidate(window))
        {
            SetStatus(new SessionStatus(SessionState.WaitingForTarget));
            do
            {
                if (!Sleep(_options.PollIntervalMs))
                {
                    return false;
                }

                window = _desktop.GetForegroundWindow();
            }
            while (!IsCandidate(window));

            // Give the user a moment to click where the text should go.
            countdownMs = _options.SwitchCountdownMs;
            return false;
        }

        if (!skipGrace && _options.GraceMs > 0)
        {
            if (!Sleep(_options.GraceMs) || _desktop.GetForegroundWindow() != window)
            {
                return false;
            }
        }

        return TrySetTarget(window);
    }

    /// <summary>Waits for the keystroke's delay, then presses and releases it. False if interrupted first.</summary>
    private bool TypeKeystroke(in Keystroke keystroke, bool shortenDelay)
    {
        KeyChord chord = ResolveKey(keystroke);
        double delay = shortenDelay ? Math.Min(keystroke.DelayBeforeMs, 150) : keystroke.DelayBeforeMs;

        if (chord.NeedsShift != _shiftHeld)
        {
            // Like a person: Shift goes down a little before the key, and comes up soon after the previous one.
            double offset = chord.NeedsShift
                ? Math.Min(delay * 0.6, _random.Between(35, 75))
                : Math.Min(delay * 0.5, _random.Between(12, 40));
            double beforeShift = chord.NeedsShift ? delay - offset : offset;

            if (!Sleep(beforeShift) || (chord.NeedsShift && !EnsureFocus()))
            {
                return false;
            }

            _keyboard.SetShift(chord.NeedsShift);
            _shiftHeld = chord.NeedsShift;
            if (!Sleep(delay - beforeShift))
            {
                return false;
            }
        }
        else if (!Sleep(delay))
        {
            return false;
        }

        if (!WaitForModifierRelease() || !EnsureFocus())
        {
            return false;
        }

        chord = ResolveKey(keystroke); // focus may have followed the user to another window
        if (_targetIsCodeEditor)
        {
            PressGuardKeys(_editorGuard.BeforeKey(keystroke));
        }

        SetShift(chord.NeedsShift);
        _keyboard.Press(chord);

        List<GuardKey> fixes = _editorGuard.AfterKey(keystroke);
        if (_targetIsCodeEditor && fixes.Count > 0)
        {
            // These must follow the key at once, while the target certainly still has focus.
            _keyboard.Release(chord);
            PressGuardKeys(fixes);
            Sleep(keystroke.HoldMs, out _, honorPause: false);
        }
        else
        {
            Sleep(keystroke.HoldMs, out _, honorPause: false);
            _keyboard.Release(chord);
        }

        return true;
    }

    /// <summary>
    /// In a code editor a tab is sent as a character: the Tab key would accept a suggestion or insert spaces instead.
    /// </summary>
    private KeyChord ResolveKey(in Keystroke keystroke) =>
        _targetIsCodeEditor && keystroke.Kind == KeystrokeKind.Tab
            ? new KeyChord(0, 0, "\t", NeedsShift: false)
            : _keyboard.Resolve(keystroke);

    private void PressGuardKeys(List<GuardKey> keys)
    {
        foreach (GuardKey key in keys)
        {
            KeyChord chord = ResolveKey(key.Key);
            if (key.Shift)
            {
                chord = chord with { NeedsShift = true };
            }

            SetShift(chord.NeedsShift);
            _keyboard.Press(chord);
            _keyboard.Release(chord);
        }
    }

    private void SetShift(bool pressed)
    {
        if (pressed != _shiftHeld)
        {
            _keyboard.SetShift(pressed);
            _shiftHeld = pressed;
        }
    }

    /// <summary>
    /// Closes the editor's suggestions before a pause: the first key after resuming can't do it, because by then
    /// the caret may be anywhere, and an Enter would accept a suggestion that was left open.
    /// </summary>
    private void CloseEditorSuggestions()
    {
        if (_targetIsCodeEditor && _desktop.GetForegroundWindow() == _target)
        {
            PressGuardKeys(_editorGuard.CloseSuggestions());
        }
    }

    /// <summary>Puts the last line's indentation back, as no Enter is coming to do it.</summary>
    private void FinishEditorLine()
    {
        if (_targetIsCodeEditor && WaitForModifierRelease() && _desktop.GetForegroundWindow() == _target)
        {
            PressGuardKeys(_editorGuard.AtEndOfText());
        }
    }

    /// <summary>Checks that the target window still has focus; requests a pause if it doesn't.</summary>
    private bool EnsureFocus()
    {
        if (_desktop.GetForegroundWindow() == _target)
        {
            return true;
        }

        // Focus can flicker briefly (tooltips, IME pop-ups), so look again before reacting.
        if (!Sleep(_options.FocusRecheckMs))
        {
            return false;
        }

        nint window = _desktop.GetForegroundWindow();
        if (window == _target)
        {
            return true;
        }

        if (!_options.PauseOnFocusLoss && IsCandidate(window))
        {
            if (!TrySetTarget(window))
            {
                return false;
            }

            SetStatus(new SessionStatus(SessionState.Typing, TargetTitle: _targetTitle));
            return true;
        }

        RequestPause(PauseReason.FocusLost);
        return false;
    }

    /// <summary>Makes <paramref name="window"/> the target, or requests a pause if it would block simulated input.</summary>
    private bool TrySetTarget(nint window)
    {
        _targetTitle = _desktop.GetWindowTitle(window);
        if (!_desktop.CanSendInputTo(window))
        {
            RequestPause(PauseReason.TargetBlocked);
            return false;
        }

        _target = window;
        _targetIsCodeEditor = _options.KeepCodeEditorTextExact && _desktop.IsCodeEditor(window);
        _editorGuard.CaretMayHaveMoved();
        return true;
    }

    /// <summary>
    /// Waits until the user lets go of Ctrl/Alt/Win: a key pressed meanwhile would become a shortcut (Ctrl+A, then a
    /// letter, would replace the whole document). False if interrupted first.
    /// </summary>
    private bool WaitForModifierRelease()
    {
        while (_keyboard.IsUserHoldingModifiers())
        {
            if (!Sleep(_options.PollIntervalMs))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsCandidate(nint window) => window != 0 && !_desktop.IsOwnWindow(window);

    private bool Sleep(double milliseconds) => Sleep(milliseconds, out _);

    /// <summary>Sleeps unless a stop (or, with <paramref name="honorPause"/>, a pause) is requested first.</summary>
    /// <returns>True if the full time elapsed.</returns>
    private bool Sleep(double milliseconds, out double slept, bool honorPause = true)
    {
        long start = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            while (true)
            {
                slept = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (_stopRequested || (honorPause && _pauseRequested))
                {
                    return false;
                }

                double remaining = milliseconds - slept;
                if (remaining <= 0)
                {
                    return true;
                }

                Monitor.Wait(_sync, (int)Math.Ceiling(remaining));
            }
        }
    }

    /// <summary>Blocks while paused. Returns false if the session was stopped instead of resumed.</summary>
    private bool WaitWhilePaused()
    {
        lock (_sync)
        {
            while (_pauseRequested && !_stopRequested)
            {
                Monitor.Wait(_sync);
            }

            return !_stopRequested;
        }
    }

    private void RequestPause(PauseReason reason)
    {
        lock (_sync)
        {
            if (_stopRequested)
            {
                return;
            }

            _pauseRequested = true;
            _pauseReason = reason;
            Monitor.PulseAll(_sync);
        }
    }

    private void ReleaseKeys()
    {
        try
        {
            _keyboard.ReleaseAll();
        }
        finally
        {
            _shiftHeld = false;
        }
    }

    private void Finish(SessionState state, string? error = null)
    {
        _typingClock.Stop();
        SetStatus(new SessionStatus(state, TargetTitle: _targetTitle, Error: error));
    }

    private void SetStatus(SessionStatus status)
    {
        lock (_sync)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
        }

        StatusChanged?.Invoke(status);
    }
}

namespace AutoKey.Core.Execution;

public enum SessionState
{
    NotStarted,

    /// <summary>Counting down so the user can switch to the window that should receive the text.</summary>
    Countdown,

    /// <summary>This application still has focus; waiting for the user to click into another window.</summary>
    WaitingForTarget,

    Typing,
    Paused,
    Completed,
    Stopped,
    Faulted,
}

public enum PauseReason
{
    None,

    /// <summary>The user asked to pause.</summary>
    User,

    /// <summary>The window being typed into lost keyboard focus.</summary>
    FocusLost,

    /// <summary>Windows would block input to the target window (it runs with higher privileges).</summary>
    TargetBlocked,
}

/// <summary>A snapshot of what a <see cref="TypingSession"/> is doing.</summary>
public sealed record SessionStatus(
    SessionState State,
    PauseReason PauseReason = PauseReason.None,
    int CountdownSeconds = 0,
    string? TargetTitle = null,
    string? Error = null)
{
    public bool IsFinished => State is SessionState.Completed or SessionState.Stopped or SessionState.Faulted;
}

/// <summary>How far a <see cref="TypingSession"/> has got.</summary>
/// <param name="CharactersTyped">Characters of the text that are correctly in place.</param>
/// <param name="CharactersTotal">Characters in the text.</param>
/// <param name="KeystrokesDone">Keystrokes sent so far, including typos and corrections.</param>
/// <param name="Elapsed">Time spent typing, excluding pauses.</param>
/// <param name="Remaining">Planned typing time still to go.</param>
/// <param name="Total">Planned typing time for the whole text.</param>
public readonly record struct SessionProgress(
    int CharactersTyped,
    int CharactersTotal,
    int KeystrokesDone,
    TimeSpan Elapsed,
    TimeSpan Remaining,
    TimeSpan Total)
{
    /// <summary>Completed share of the planned typing time, from 0 to 1.</summary>
    public double Fraction => Total <= TimeSpan.Zero ? 1 : Math.Clamp(1 - (Remaining / Total), 0, 1);
}

public sealed record SessionOptions
{
    /// <summary>Seconds to wait before typing starts.</summary>
    public double CountdownSeconds { get; init; } = 3;

    /// <summary>
    /// Pause when the target window loses focus. When false, typing follows focus to other windows;
    /// it always pauses rather than type into this application's own windows.
    /// </summary>
    public bool PauseOnFocusLoss { get; init; } = true;

    /// <summary>
    /// In code editors, send the extra keys that stop autocomplete, auto-indent and auto-closing brackets from
    /// changing the text.
    /// </summary>
    public bool KeepCodeEditorTextExact { get; init; } = true;

    /// <summary>Delay before typing starts or resumes when no countdown preceded it.</summary>
    public int GraceMs { get; init; } = 400;

    /// <summary>Countdown after the user switches away from this application, so they can place the caret.</summary>
    public int SwitchCountdownMs { get; init; } = 1500;

    /// <summary>How often to check the foreground window while waiting for the user.</summary>
    public int PollIntervalMs { get; init; } = 50;

    /// <summary>How long to wait before deciding that a focus change is real rather than a flicker.</summary>
    public int FocusRecheckMs { get; init; } = 60;
}

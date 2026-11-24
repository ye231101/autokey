using System.Text;

namespace AutoKey.Core.Planning;

public enum KeystrokeKind : byte
{
    Character,
    Backspace,
    Enter,
    Tab,

    // Editing keys: never part of a plan, only used to stop code editors from changing the text.
    Left,
    Right,
    Home,
    Delete,
}

/// <summary>One key press in a <see cref="TypingPlan"/>.</summary>
/// <param name="Kind">What the key does.</param>
/// <param name="Character">The character typed when <paramref name="Kind"/> is <see cref="KeystrokeKind.Character"/>.</param>
/// <param name="DelayBeforeMs">Time between releasing the previous key and pressing this one.</param>
/// <param name="HoldMs">How long the key stays down.</param>
/// <param name="IsTypo">True when the keystroke is part of a mistake that gets deleted again.</param>
/// <param name="Progress">How many characters of the text are correctly in place after this keystroke.</param>
public readonly record struct Keystroke(
    KeystrokeKind Kind,
    Rune Character,
    double DelayBeforeMs,
    double HoldMs,
    bool IsTypo,
    int Progress);

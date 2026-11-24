namespace AutoKey.Core.Planning;

/// <summary>A precomputed sequence of keystrokes that types a text, including typos and their corrections.</summary>
public sealed class TypingPlan
{
    private readonly Keystroke[] _keystrokes;

    /// <summary><c>_remainingMs[i]</c> is the time needed for keystrokes <c>i..end</c>.</summary>
    private readonly double[] _remainingMs;

    public TypingPlan(string text, int characterCount, IReadOnlyList<Keystroke> keystrokes, int typoCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(keystrokes);

        Text = text;
        CharacterCount = characterCount;
        TypoCount = typoCount;
        _keystrokes = keystrokes.ToArray();
        _remainingMs = new double[_keystrokes.Length + 1];
        for (int i = _keystrokes.Length - 1; i >= 0; i--)
        {
            _remainingMs[i] = _remainingMs[i + 1] + _keystrokes[i].DelayBeforeMs + _keystrokes[i].HoldMs;
        }
    }

    /// <summary>The normalized text this plan types.</summary>
    public string Text { get; }

    /// <summary>Number of characters (Unicode scalar values) in <see cref="Text"/>.</summary>
    public int CharacterCount { get; }

    /// <summary>Number of typos the plan makes (and corrects).</summary>
    public int TypoCount { get; }

    public IReadOnlyList<Keystroke> Keystrokes => _keystrokes;

    public int Count => _keystrokes.Length;

    public Keystroke this[int index] => _keystrokes[index];

    /// <summary>How long the whole plan takes to type.</summary>
    public TimeSpan EstimatedDuration => TimeSpan.FromMilliseconds(_remainingMs[0]);

    /// <summary>How long it takes to type the keystrokes from <paramref name="index"/> onwards.</summary>
    public TimeSpan RemainingFrom(int index) =>
        TimeSpan.FromMilliseconds(_remainingMs[Math.Clamp(index, 0, _keystrokes.Length)]);
}

/// <summary>What typing a text is expected to involve, averaged over several simulated plans.</summary>
/// <param name="CharacterCount">Number of characters (Unicode scalar values) in the normalized text.</param>
/// <param name="Duration">Average planned typing time.</param>
/// <param name="TypoCount">Average number of typos, rounded to a whole number.</param>
public readonly record struct PlanEstimate(int CharacterCount, TimeSpan Duration, int TypoCount);

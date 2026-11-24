namespace AutoKey.Core.Planning;

/// <summary>Describes the simulated typist.</summary>
public sealed record TypingProfile
{
    /// <summary>Typing speed in words per minute, where a word is five keystrokes.</summary>
    public double WordsPerMinute { get; init; } = 60;

    /// <summary>How uneven the rhythm is, from 0 (metronome-steady) to 1 (very erratic).</summary>
    public double Variability { get; init; } = 0.5;

    /// <summary>Probability (0–1) that a letter or digit is mistyped and then corrected.</summary>
    public double TypoRate { get; init; } = 0.03;

    /// <summary>Multiplier for natural pauses at sentence ends, line breaks and "thinking" moments; 0 disables them.</summary>
    public double PauseScale { get; init; } = 1.0;

    /// <summary>Probability that a typo is noticed right away rather than a few keystrokes later.</summary>
    public double ImmediateNoticeChance { get; init; } = 0.55;

    public static TypingProfile Default { get; } = new();

    /// <summary>Returns a copy with every value clamped to a usable range.</summary>
    public TypingProfile Sanitized() => this with
    {
        WordsPerMinute = Clamp(WordsPerMinute, 5, 300, 60),
        Variability = Clamp(Variability, 0, 1, 0.5),
        TypoRate = Clamp(TypoRate, 0, 0.5, 0.03),
        PauseScale = Clamp(PauseScale, 0, 5, 1),
        ImmediateNoticeChance = Clamp(ImmediateNoticeChance, 0, 1, 0.55),
    };

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

using System.Text;
using AutoKey.Core.Planning;
using AutoKey.Core.Text;

namespace AutoKey.Core.Tests;

public class HumanTypingPlannerTests
{
    private const string Paragraph =
        "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs! " +
        "How vexingly quick daft zebras jump; sphinx of black quartz, judge my vow. " +
        "Typing like a person means hesitating, speeding up, and fixing mistakes along the way.";

    private static readonly string[] Samples =
    [
        "",
        "a",
        "Hello, World!",
        Paragraph,
        "Line one\r\nLine two\rLine three\n\nNew paragraph\twith a tab.",
        "Emoji 👋🏽 and accents: café, naïve, Ærøskøbing, ß, İstanbul",
        "UPPER lower MiXeD THe AND 12345 67890 3.14159 e-mail@example.com (brackets) [square] {curly}",
        "Привет, как дела? Γειά σου κόσμε. 日本語のテキスト。",
        "don't won't it's rock'n'roll — “smart quotes” and ‘single’",
        "    indented\n\tcode();\n}",
        "aa bb cc AA BB ll oo ee tt ss",
    ];

    public static TheoryData<string> Texts => new(Samples);

    [Theory]
    [MemberData(nameof(Texts))]
    public void Replaying_a_plan_reproduces_the_text_exactly_at_any_typo_rate(string text)
    {
        string expected = TextNormalizer.Normalize(text);
        foreach (double typoRate in new[] { 0.0, 0.03, 0.2, 0.5 })
        {
            var planner = new HumanTypingPlanner(new TypingProfile { TypoRate = typoRate });
            for (int seed = 0; seed < 50; seed++)
            {
                string actual = planner.CreatePlan(text, seed).Replay();
                if (actual != expected)
                {
                    Assert.Fail($"Typo rate {typoRate}, seed {seed}: expected \"{expected}\" but replay gave \"{actual}\".");
                }
            }
        }
    }

    [Fact]
    public void No_typos_or_backspaces_when_the_typo_rate_is_zero()
    {
        var plan = new HumanTypingPlanner(new TypingProfile { TypoRate = 0 }).CreatePlan(Paragraph, seed: 1);

        Assert.Equal(0, plan.TypoCount);
        Assert.DoesNotContain(plan.Keystrokes, k => k.Kind == KeystrokeKind.Backspace || k.IsTypo);
        Assert.Equal(plan.CharacterCount, plan.Count);
    }

    [Fact]
    public void Typos_are_made_and_corrected_at_high_rates()
    {
        var plan = new HumanTypingPlanner(new TypingProfile { TypoRate = 0.2 }).CreatePlan(Paragraph, seed: 7);

        Assert.True(plan.TypoCount > 10, $"Expected plenty of typos, got {plan.TypoCount}.");
        int backspaces = plan.Keystrokes.Count(k => k.Kind == KeystrokeKind.Backspace);
        Assert.True(backspaces >= plan.TypoCount);
        Assert.Equal(TextNormalizer.Normalize(Paragraph), plan.Replay());
    }

    [Fact]
    public void Backspace_only_ever_deletes_typo_keystrokes()
    {
        for (int seed = 0; seed < 30; seed++)
        {
            var plan = new HumanTypingPlanner(new TypingProfile { TypoRate = 0.25 }).CreatePlan(Paragraph, seed);
            var typed = new Stack<Keystroke>();
            foreach (Keystroke keystroke in plan.Keystrokes)
            {
                if (keystroke.Kind == KeystrokeKind.Backspace)
                {
                    Assert.True(typed.Pop().IsTypo, $"Seed {seed}: Backspace deleted correctly typed text.");
                }
                else
                {
                    typed.Push(keystroke);
                }
            }
        }
    }

    [Fact]
    public void Typos_stay_inside_words()
    {
        // Mistakes (and the letters typed before noticing them) are letters or digits only, so the correction
        // never crosses a space, punctuation or a line break where autocorrect or auto-indent could interfere.
        for (int seed = 0; seed < 30; seed++)
        {
            var plan = new HumanTypingPlanner(new TypingProfile { TypoRate = 0.3 }).CreatePlan(string.Join(' ', Samples), seed);
            foreach (Keystroke keystroke in plan.Keystrokes.Where(k => k.IsTypo))
            {
                Assert.Equal(KeystrokeKind.Character, keystroke.Kind);
                Assert.True(Rune.IsLetterOrDigit(keystroke.Character), $"Seed {seed}: unexpected typo character {keystroke.Character}.");
            }
        }
    }

    [Fact]
    public void Progress_never_decreases_and_ends_at_the_character_count()
    {
        var plan = new HumanTypingPlanner(new TypingProfile { TypoRate = 0.2 }).CreatePlan(Paragraph, seed: 3);

        int previous = 0;
        foreach (Keystroke keystroke in plan.Keystrokes)
        {
            Assert.True(keystroke.Progress >= previous);
            previous = keystroke.Progress;
        }

        Assert.Equal(plan.CharacterCount, plan.Keystrokes[^1].Progress);
    }

    [Fact]
    public void The_same_seed_gives_the_same_plan()
    {
        var planner = new HumanTypingPlanner();

        var first = planner.CreatePlan(Paragraph, seed: 42);
        var second = planner.CreatePlan(Paragraph, seed: 42);
        var other = planner.CreatePlan(Paragraph, seed: 43);

        Assert.Equal(first.Keystrokes, second.Keystrokes);
        Assert.NotEqual(first.Keystrokes, other.Keystrokes);
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void An_estimate_is_the_average_of_the_plans_it_samples(string text)
    {
        var planner = new HumanTypingPlanner(new TypingProfile { TypoRate = 0.1 });

        PlanEstimate estimate = planner.Estimate(text, samples: 3, seed: 100);

        TypingPlan[] plans = [planner.CreatePlan(text, 100), planner.CreatePlan(text, 101), planner.CreatePlan(text, 102)];
        Assert.Equal(plans[0].CharacterCount, estimate.CharacterCount);
        Assert.Equal((int)Math.Round(plans.Average(p => p.TypoCount)), estimate.TypoCount);
        Assert.Equal(plans.Average(p => p.EstimatedDuration.TotalMilliseconds), estimate.Duration.TotalMilliseconds, tolerance: 0.001);
    }

    [Fact]
    public void A_cancelled_estimate_stops()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new HumanTypingPlanner().Estimate(Paragraph, samples: 1, seed: 0, cancellation.Token));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(100)]
    [InlineData(160)]
    public void Average_speed_matches_the_requested_words_per_minute(double wpm)
    {
        var profile = new TypingProfile { WordsPerMinute = wpm, TypoRate = 0, PauseScale = 0 };
        string text = string.Concat(Enumerable.Repeat(Paragraph + " ", 10));

        var plan = new HumanTypingPlanner(profile).CreatePlan(text, seed: 5);

        double actualMsPerChar = plan.EstimatedDuration.TotalMilliseconds / plan.CharacterCount;
        double expectedMsPerChar = 12_000 / wpm;
        Assert.InRange(actualMsPerChar, expectedMsPerChar * 0.85, expectedMsPerChar * 1.15);
    }

    [Fact]
    public void Natural_pauses_make_typing_slower_and_can_be_turned_off()
    {
        string text = string.Concat(Enumerable.Repeat(Paragraph + "\n", 5));
        TimeSpan Duration(double pauses) => new HumanTypingPlanner(new TypingProfile { TypoRate = 0, PauseScale = pauses })
            .CreatePlan(text, seed: 9).EstimatedDuration;

        Assert.True(Duration(1) > Duration(0) * 1.1);
        Assert.True(Duration(2) > Duration(1));
    }

    [Fact]
    public void Sentences_start_after_longer_pauses_than_ordinary_words()
    {
        string text = string.Concat(Enumerable.Repeat("This is a short sentence with words. ", 60));
        var plan = new HumanTypingPlanner(new TypingProfile { TypoRate = 0 }).CreatePlan(text, seed: 11);
        string normalized = plan.Text;

        // Without typos, keystroke i types character i.
        var sentenceStarts = new List<double>();
        var wordStarts = new List<double>();
        for (int i = 2; i < normalized.Length; i++)
        {
            if (normalized[i - 1] != ' ' || !char.IsLetter(normalized[i]))
            {
                continue;
            }

            (normalized[i - 2] == '.' ? sentenceStarts : wordStarts).Add(plan[i].DelayBeforeMs);
        }

        Assert.True(sentenceStarts.Average() > wordStarts.Average() * 2,
            $"Sentence starts averaged {sentenceStarts.Average():0} ms, word starts {wordStarts.Average():0} ms.");
    }

    [Fact]
    public void Keys_are_held_briefly_and_released_before_the_next_press()
    {
        var plan = new HumanTypingPlanner(new TypingProfile { WordsPerMinute = 200, TypoRate = 0.1 }).CreatePlan(Paragraph, seed: 2);

        Assert.All(plan.Keystrokes, k => Assert.InRange(k.HoldMs, 25, 250));
        Assert.All(plan.Keystrokes.Skip(1), k => Assert.True(k.DelayBeforeMs >= 5.9));
    }

    [Fact]
    public void Line_breaks_tabs_and_emoji_become_single_keystrokes()
    {
        var plan = new HumanTypingPlanner(new TypingProfile { TypoRate = 0 }).CreatePlan("a\r\n\t👋");

        Assert.Equal(
            [KeystrokeKind.Character, KeystrokeKind.Enter, KeystrokeKind.Tab, KeystrokeKind.Character],
            plan.Keystrokes.Select(k => k.Kind));
        Assert.Equal(0x1F44B, plan[3].Character.Value);
        Assert.Equal("a\n\t👋", plan.Replay());
    }

    [Fact]
    public void Empty_text_gives_an_empty_plan()
    {
        var plan = new HumanTypingPlanner().CreatePlan("");

        Assert.Equal(0, plan.Count);
        Assert.Equal(TimeSpan.Zero, plan.EstimatedDuration);
    }

    [Fact]
    public void Out_of_range_profile_values_are_clamped()
    {
        var profile = new TypingProfile { WordsPerMinute = 0, TypoRate = double.NaN, PauseScale = -3, Variability = 7 }.Sanitized();

        Assert.Equal(5, profile.WordsPerMinute);
        Assert.Equal(0.03, profile.TypoRate);
        Assert.Equal(0, profile.PauseScale);
        Assert.Equal(1, profile.Variability);
    }
}

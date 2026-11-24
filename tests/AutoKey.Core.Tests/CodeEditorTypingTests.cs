using System.Diagnostics;
using AutoKey.Core.Execution;
using AutoKey.Core.Planning;

namespace AutoKey.Core.Tests;

public class CodeEditorTypingTests
{
    // Repeated words (autocomplete), nested and unbalanced brackets, quotes, tabs, odd indentation, blank lines,
    // whitespace-only lines, trailing spaces and a doc comment.
    private const string Code =
        "/**\n" +
        " * Adds \"two\" numbers, doesn't it?\n" +
        " */\n" +
        "function add(alpha, beta) {\n" +
        "\tconst total = alpha + beta;   \n" +
        "    if (total > 10) {\n" +
        "        return [total, 'big'];\n" +
        "            }\n" +
        "\n" +
        "    \n" +
        "    return total;\n" +
        "}\n" +
        "add(1,\n" +
        "    2\n" +
        "  )\n" +
        ")\n" +
        "  ]\n" +
        "\"open quote\n" +
        "(open [bracket {brace\n" +
        "Remove\n" +
        "        Hourly\n" +
        "Remove\n" +
        "Fixed-Price\n" +
        "\ttab\tseparated\tvalues\n" +
        "begin\n" +
        "        body\n" +
        "             end\n" +
        "  ending soon\n" +
        "switch (x) {\n" +
        "          case 1:\n" +
        "     when ready\n" +
        "              }\n" +
        "emoji 👋 done\n" +
        "        nested\n" +
        "             end";

    private static readonly SessionOptions Options = new()
    {
        CountdownSeconds = 0,
        GraceMs = 0,
        SwitchCountdownMs = 20,
        PollIntervalMs = 5,
        FocusRecheckMs = 5,
    };

    private static TypingPlan Plan(string text, double typoRate, int seed)
    {
        TypingPlan plan = new HumanTypingPlanner(new TypingProfile { TypoRate = typoRate }).CreatePlan(text, seed);
        var instant = plan.Keystrokes.Select(k => k with { DelayBeforeMs = 0, HoldMs = 0 }).ToList();
        return new TypingPlan(plan.Text, plan.CharacterCount, instant, plan.TypoCount);
    }

    private static void RunToCompletion(TypingSession session)
    {
        session.Start();
        Assert.True(session.WaitForExit(TimeSpan.FromSeconds(30)), "The session did not finish.");
        Assert.Equal(SessionState.Completed, session.Status.State);
    }

    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(0.05, 2)]
    [InlineData(0.05, 3)]
    [InlineData(0.15, 4)]
    [InlineData(0.15, 5)]
    [InlineData(0.3, 6)]
    public void A_code_editor_ends_up_with_exactly_the_input_text(double typoRate, int seed)
    {
        var editor = new FakeCodeEditor();
        var session = new TypingSession(Plan(Code, typoRate, seed), editor, new FakeDesktop { CodeEditor = true }, Options);

        RunToCompletion(session);

        Assert.Equal(Code, editor.Text);
    }

    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(0.1, 2)]
    public void Retyping_the_indentation_does_not_set_off_a_rule_of_its_own(double typoRate, int seed)
    {
        var editor = new FakeCodeEditor { IndentedBraceRule = true };
        var session = new TypingSession(Plan(Code, typoRate, seed), editor, new FakeDesktop { CodeEditor = true }, Options);

        RunToCompletion(session);

        Assert.Equal(Code, editor.Text);
    }

    [Fact]
    public void With_the_option_off_the_editor_changes_the_text()
    {
        var editor = new FakeCodeEditor();
        var session = new TypingSession(
            Plan(Code, 0, 1), editor, new FakeDesktop { CodeEditor = true }, Options with { KeepCodeEditorTextExact = false });

        RunToCompletion(session);

        Assert.NotEqual(Code, editor.Text); // shows that the model really does what editors do
    }

    [Fact]
    public void Other_windows_get_only_the_planned_keys()
    {
        var keyboard = new FakeKeyboard();
        TypingPlan plan = Plan(Code, 0.05, 2);
        var session = new TypingSession(plan, keyboard, new FakeDesktop(), Options);

        RunToCompletion(session);

        Assert.Equal(Code, keyboard.Output);
        Assert.Equal(plan.Count, keyboard.Presses);
    }

    [Fact]
    public void A_suggestion_left_open_by_a_pause_does_not_swallow_the_next_line_break()
    {
        const string text = "alpha\nalpha\nomega";
        var editor = new FakeCodeEditor();
        var session = new TypingSession(Plan(text, 0, 1), editor, new FakeDesktop { CodeEditor = true }, Options);
        bool paused = false;
        editor.AfterPress = () =>
        {
            if (!paused && editor.Text == "alpha\nalpha")
            {
                paused = true;
                session.Pause(); // the list of suggestions is open at this moment
            }
        };

        session.Start();
        var watch = Stopwatch.StartNew();
        while (session.Status.State != SessionState.Paused)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "The session did not pause.");
            Thread.Sleep(2);
        }

        session.Resume();
        Assert.True(session.WaitForExit(TimeSpan.FromSeconds(15)), "The session did not finish.");
        Assert.Equal(SessionState.Completed, session.Status.State);
        Assert.Equal(text, editor.Text);
    }

    [Fact]
    public void Text_already_on_the_first_line_is_left_alone()
    {
        // The session starts with the caret at the end of existing text: the guard must not select back into it.
        var editor = new FakeCodeEditor();
        foreach (char c in "keep this ")
        {
            KeyChord chord = editor.Resolve(new Keystroke(KeystrokeKind.Character, new System.Text.Rune(c), 0, 0, false, 0));
            editor.SetShift(chord.NeedsShift);
            editor.Press(chord);
        }

        editor.SetShift(false);
        var session = new TypingSession(Plan("end of line\nnext", 0, 1), editor, new FakeDesktop { CodeEditor = true }, Options);

        RunToCompletion(session);

        Assert.Equal("keep this end of line\nnext", editor.Text);
    }

    [Fact]
    public void Focus_moving_to_a_code_editor_mid_text_is_handled()
    {
        var editor = new FakeCodeEditor();
        var desktop = new FakeDesktop { CodeEditor = true };
        var session = new TypingSession(Plan("first line\n    second (line)\n", 0, 1), editor, desktop, Options with { PauseOnFocusLoss = false });
        bool moved = false;
        editor.AfterPress = () =>
        {
            if (!moved && editor.Text.EndsWith("first", StringComparison.Ordinal))
            {
                moved = true;
                desktop.Foreground = 200;
            }
        };

        RunToCompletion(session);

        Assert.Equal("first line\n    second (line)\n", editor.Text);
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using AutoKey.Core.Execution;
using AutoKey.Core.Planning;

namespace AutoKey.Core.Tests;

public class TypingSessionTests
{
    private const string Text = "Hello World! This SESSION types Mixed Case text,\nwith a second line and some typos.";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly SessionOptions FastOptions = new()
    {
        CountdownSeconds = 0,
        GraceMs = 0,
        SwitchCountdownMs = 20,
        PollIntervalMs = 5,
        FocusRecheckMs = 5,
    };

    static TypingSessionTests()
    {
        // The app raises the Windows timer resolution to 1 ms while typing (the default is 15.6 ms);
        // do the same here so waits behave as they do in the app.
        if (OperatingSystem.IsWindows())
        {
            _ = timeBeginPeriod(1);
        }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    /// <summary>A realistic plan (with typos) sped up so tests run quickly.</summary>
    private static TypingPlan FastPlan(string text = Text, double typoRate = 0.15, double speed = 0.01)
    {
        var plan = new HumanTypingPlanner(new TypingProfile { TypoRate = typoRate }).CreatePlan(text, seed: 1);
        var keystrokes = plan.Keystrokes
            .Select(k => k with { DelayBeforeMs = k.DelayBeforeMs * speed, HoldMs = k.HoldMs * speed })
            .ToList();
        return new TypingPlan(plan.Text, plan.CharacterCount, keystrokes, plan.TypoCount);
    }

    private static (TypingSession Session, FakeKeyboard Keyboard, FakeDesktop Desktop, ConcurrentQueue<SessionStatus> Statuses) Create(
        TypingPlan? plan = null, SessionOptions? options = null)
    {
        var keyboard = new FakeKeyboard();
        var desktop = new FakeDesktop();
        var session = new TypingSession(plan ?? FastPlan(), keyboard, desktop, options ?? FastOptions);
        var statuses = new ConcurrentQueue<SessionStatus>();
        session.StatusChanged += statuses.Enqueue;
        return (session, keyboard, desktop, statuses);
    }

    private static void WaitUntil(Func<bool> condition, string description)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Timeout)
            {
                Assert.Fail($"Timed out waiting until {description}.");
            }

            Thread.Sleep(2);
        }
    }

    private static void AssertCompleted(TypingSession session, FakeKeyboard keyboard)
    {
        Assert.True(session.WaitForExit(Timeout), "The session did not finish.");
        Assert.Equal(SessionState.Completed, session.Status.State);
        Assert.Equal(session.Plan.Text, keyboard.Output);
        Assert.Equal(0, keyboard.HeldCount);
        Assert.False(keyboard.ShiftDown);
    }

    [Fact]
    public void Types_the_whole_plan_including_corrections()
    {
        var (session, keyboard, _, statuses) = Create();

        session.Start();

        AssertCompleted(session, keyboard);
        Assert.Equal(session.Plan.Count, keyboard.Presses);
        Assert.Contains(statuses, s => s.State == SessionState.Typing && s.TargetTitle == "Window 100");
    }

    [Fact]
    public void Shift_is_held_exactly_while_shifted_characters_are_pressed()
    {
        var (session, keyboard, _, _) = Create(FastPlan("Hello WORLD, Abc dEF GhI.", typoRate: 0.3));

        session.Start();

        AssertCompleted(session, keyboard);
        Assert.Equal(0, keyboard.ShiftViolations);
    }

    [Fact]
    public void Reports_progress_up_to_the_whole_text()
    {
        var (session, keyboard, _, _) = Create();
        var progress = new ConcurrentQueue<SessionProgress>();
        session.ProgressChanged += progress.Enqueue;

        session.Start();

        AssertCompleted(session, keyboard);
        SessionProgress last = progress.Last();
        Assert.Equal(1, last.Fraction);
        Assert.Equal(session.Plan.CharacterCount, last.CharactersTyped);
        Assert.Equal(session.Plan.Count, last.KeystrokesDone);
    }

    [Fact]
    public void Counts_down_before_typing()
    {
        var (session, keyboard, _, statuses) = Create(options: FastOptions with { CountdownSeconds = 1.2 });

        session.Start();

        AssertCompleted(session, keyboard);
        var countdown = statuses.Where(s => s.State == SessionState.Countdown).Select(s => s.CountdownSeconds).ToList();
        Assert.Equal([2, 1], countdown);
    }

    [Fact]
    public void Waits_while_its_own_window_has_focus()
    {
        var (session, keyboard, desktop, _) = Create();
        desktop.Foreground = FakeDesktop.OwnWindow;

        session.Start();
        WaitUntil(() => session.Status.State == SessionState.WaitingForTarget, "it waits for the target");
        Thread.Sleep(50);
        Assert.Equal(0, keyboard.Presses);

        desktop.Foreground = FakeDesktop.Editor;
        AssertCompleted(session, keyboard);
    }

    [Fact]
    public void User_can_pause_and_resume()
    {
        var (session, keyboard, _, _) = Create(FastPlan(speed: 0.05));
        keyboard.AfterPress = presses =>
        {
            if (presses == 10)
            {
                session.Pause();
            }
        };

        session.Start();
        WaitUntil(() => session.Status.State == SessionState.Paused, "it pauses");
        Assert.Equal(PauseReason.User, session.Status.PauseReason);
        Assert.Equal(0, keyboard.HeldCount);
        Assert.False(keyboard.ShiftDown);

        int pressesWhilePaused = keyboard.Presses;
        Thread.Sleep(100);
        Assert.Equal(pressesWhilePaused, keyboard.Presses);

        session.Resume();
        AssertCompleted(session, keyboard);
    }

    [Fact]
    public void Pauses_when_focus_moves_away_and_resumes_in_the_newly_focused_window()
    {
        var (session, keyboard, desktop, statuses) = Create();
        keyboard.AfterPress = presses =>
        {
            if (presses == 15)
            {
                desktop.Foreground = 200;
            }
        };

        session.Start();
        WaitUntil(() => session.Status.State == SessionState.Paused, "it pauses");
        Assert.Equal(PauseReason.FocusLost, session.Status.PauseReason);
        Assert.Equal(15, keyboard.Presses);

        session.Resume();
        AssertCompleted(session, keyboard);
        Assert.Contains(statuses, s => s.State == SessionState.Typing && s.TargetTitle == "Window 200");
    }

    [Fact]
    public void Follows_focus_to_another_window_when_the_focus_guard_is_off()
    {
        var (session, keyboard, desktop, statuses) = Create(options: FastOptions with { PauseOnFocusLoss = false });
        keyboard.AfterPress = presses =>
        {
            if (presses == 15)
            {
                desktop.Foreground = 200;
            }
        };

        session.Start();

        AssertCompleted(session, keyboard);
        Assert.DoesNotContain(statuses, s => s.State == SessionState.Paused);
        Assert.Contains(statuses, s => s.State == SessionState.Typing && s.TargetTitle == "Window 200");
    }

    [Fact]
    public void Never_types_into_its_own_window_even_with_the_focus_guard_off()
    {
        var (session, keyboard, desktop, _) = Create(options: FastOptions with { PauseOnFocusLoss = false });
        keyboard.AfterPress = presses =>
        {
            if (presses == 15)
            {
                desktop.Foreground = FakeDesktop.OwnWindow;
            }
        };

        session.Start();
        WaitUntil(() => session.Status.State == SessionState.Paused, "it pauses");
        Assert.Equal(PauseReason.FocusLost, session.Status.PauseReason);
        Assert.Equal(15, keyboard.Presses);

        desktop.Foreground = FakeDesktop.Editor;
        session.Resume();
        AssertCompleted(session, keyboard);
    }

    [Fact]
    public void Resuming_from_its_own_window_waits_for_the_user_to_switch_back()
    {
        var (session, keyboard, desktop, _) = Create(FastPlan(speed: 0.05));
        keyboard.AfterPress = presses =>
        {
            if (presses == 10)
            {
                session.Pause();
            }
        };

        session.Start();
        WaitUntil(() => session.Status.State == SessionState.Paused, "it pauses");

        // The user clicks Resume in AutoKey itself, so AutoKey has focus.
        desktop.Foreground = FakeDesktop.OwnWindow;
        session.Resume();
        WaitUntil(() => session.Status.State == SessionState.WaitingForTarget, "it waits for the target");
        Assert.Equal(10, keyboard.Presses);

        desktop.Foreground = FakeDesktop.Editor;
        AssertCompleted(session, keyboard);
    }

    [Fact]
    public void Stop_ends_the_session_and_releases_every_key()
    {
        var (session, keyboard, _, _) = Create(FastPlan(speed: 0.05));
        keyboard.AfterPress = presses =>
        {
            if (presses == 12)
            {
                session.Stop();
            }
        };

        session.Start();

        Assert.True(session.WaitForExit(Timeout));
        Assert.Equal(SessionState.Stopped, session.Status.State);
        Assert.Equal(12, keyboard.Presses);
        Assert.Equal(0, keyboard.HeldCount);
        Assert.False(keyboard.ShiftDown);
    }

    [Fact]
    public void Stop_while_paused_ends_the_session()
    {
        var (session, keyboard, _, _) = Create();
        keyboard.AfterPress = presses =>
        {
            if (presses == 5)
            {
                session.Pause();
            }
        };

        session.Start();
        WaitUntil(() => session.Status.State == SessionState.Paused, "it pauses");
        session.Stop();

        Assert.True(session.WaitForExit(Timeout));
        Assert.Equal(SessionState.Stopped, session.Status.State);
    }

    [Fact]
    public void Pauses_instead_of_typing_into_a_window_that_blocks_input()
    {
        var (session, keyboard, desktop, _) = Create();
        desktop.Blocked[FakeDesktop.Editor] = true;

        session.Start();
        WaitUntil(() => session.Status.State == SessionState.Paused, "it pauses");
        Assert.Equal(PauseReason.TargetBlocked, session.Status.PauseReason);
        Assert.Equal("Window 100", session.Status.TargetTitle);
        Assert.Equal(0, keyboard.Presses);

        desktop.Foreground = 300;
        session.Resume();
        AssertCompleted(session, keyboard);
    }

    [Fact]
    public void Waits_until_the_user_lets_go_of_modifier_keys()
    {
        var (session, keyboard, _, _) = Create();
        keyboard.HoldingModifiers = true;

        session.Start();
        Thread.Sleep(80);
        Assert.Equal(0, keyboard.Presses);

        keyboard.HoldingModifiers = false;
        AssertCompleted(session, keyboard);
    }

    [Fact]
    public void Holds_off_while_the_user_presses_a_modifier_mid_session()
    {
        var (session, keyboard, _, _) = Create(FastPlan(speed: 0.05));
        keyboard.AfterPress = presses =>
        {
            if (presses == 10)
            {
                keyboard.HoldingModifiers = true; // the user presses Ctrl while AutoKey is typing
            }
        };

        session.Start();
        WaitUntil(() => keyboard.Presses >= 10, "ten keys are pressed"); // slow build machines need longer than you'd think
        Thread.Sleep(150);
        Assert.Equal(10, keyboard.Presses);
        Assert.Equal(SessionState.Typing, session.Status.State);

        keyboard.HoldingModifiers = false;
        AssertCompleted(session, keyboard);
    }

    [Fact]
    public void Toggle_pause_switches_between_paused_and_running()
    {
        var (session, keyboard, _, _) = Create(FastPlan(speed: 0.05));
        keyboard.AfterPress = presses =>
        {
            if (presses == 8)
            {
                session.TogglePause();
            }
        };

        session.Start();
        WaitUntil(() => session.Status.State == SessionState.Paused, "it pauses");

        session.TogglePause();
        AssertCompleted(session, keyboard);
    }

    [Fact]
    public void A_session_can_only_be_started_once()
    {
        var (session, keyboard, _, _) = Create();
        session.Start();

        Assert.Throws<InvalidOperationException>(session.Start);
        AssertCompleted(session, keyboard);
    }
}

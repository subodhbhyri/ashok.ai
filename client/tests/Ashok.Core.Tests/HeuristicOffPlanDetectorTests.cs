using Ashok.Core.Contracts;
using Ashok.Core.Evaluation;
using Ashok.Core.Session;

namespace Ashok.Core.Tests;

public class HeuristicOffPlanDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly PlanStep ClickStep = new("s1", "Click Bluetooth.", ActionKind.Click,
        new Target(null, "Button", "Bluetooth", null), null,
        new DoneWhen(DoneKind.ToggleState, null, null, new Target(null, "Button", "Bluetooth", null), "on"));

    private static readonly PlanStep KeysStep = new("s1", "Press Windows and I.", ActionKind.PressKeys, null, "Win+I",
        new DoneWhen(DoneKind.WindowOpened, "SystemSettings", null, null, null));

    private static readonly PlanStep TypeStep = new("s1", "Type a name.", ActionKind.Type,
        new Target(null, "Edit", "Name", null), null, new DoneWhen(DoneKind.UserConfirms, null, null, null, null));

    private static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SystemSettings" };

    private sealed class Clock(HeuristicOffPlanDetector detector, PlanStep step)
    {
        public DateTimeOffset Now = T0;
        public DateTimeOffset StepStart = T0;
        public PlanStep Step = step;

        public HeuristicOffPlanDetector Detector() => detector;

        public OffPlanVerdict At(TimeSpan after, string process = "SystemSettings", string title = "Settings", long handle = 1, bool? target = true)
        {
            Now = T0 + after;
            return detector.Check(new OffPlanContext(new ForegroundState(process, title, handle), Known, Step, StepStart, StepStart, Now, target));
        }
    }

    private static (HeuristicOffPlanDetector Detector, Clock Clock) Start(PlanStep? step = null)
    {
        var detector = new HeuristicOffPlanDetector();
        detector.Reset(T0);
        return (detector, new Clock(detector, step ?? ClickStep));
    }

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    // ---- another app ----

    [Fact]
    public void Another_app_in_front_is_off_plan_within_two_seconds()
    {
        var (_, c) = Start();
        c.At(S(0));

        Assert.Equal(OffPlanVerdict.OnPlan, c.At(Ms(250), "chrome", "News", 7));
        Assert.Equal(OffPlanVerdict.OnPlan, c.At(Ms(1500), "chrome", "News", 7));
        Assert.Equal(OffPlanVerdict.OffPlan, c.At(Ms(1750), "chrome", "News", 7));
    }

    [Fact]
    public void A_brief_visit_to_another_app_is_not_off_plan()
    {
        var (_, c) = Start();
        c.At(S(0));
        c.At(Ms(250), "chrome", "News", 7);
        c.At(Ms(1000), "chrome", "News", 7);

        Assert.Equal(OffPlanVerdict.OnPlan, c.At(Ms(1250)));
        Assert.Equal(OffPlanVerdict.OnPlan, c.At(S(3)));
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("SearchHost")]
    [InlineData("StartMenuExperienceHost")]
    [InlineData("ShellExperienceHost")]
    [InlineData("consent")]
    [InlineData("none")]
    public void Windows_shell_surfaces_never_count_as_another_app(string process)
    {
        var (_, c) = Start();
        c.At(S(0));

        for (var ms = 250; ms <= 10_000; ms += 250)
            Assert.Equal(OffPlanVerdict.OnPlan, c.At(Ms(ms), process, "", 5));
    }

    [Fact]
    public void Hopping_between_unexpected_windows_restarts_the_wait()
    {
        var (_, c) = Start();
        c.At(S(0));
        c.At(Ms(250), "chrome", "News", 7);

        Assert.Equal(OffPlanVerdict.OnPlan, c.At(Ms(1500), "notepad", "Untitled", 8));
        Assert.Equal(OffPlanVerdict.OffPlan, c.At(Ms(3000), "notepad", "Untitled", 8));
    }

    // ---- left the page ----

    [Fact]
    public void A_target_that_was_on_screen_and_stays_gone_is_off_plan()
    {
        var (_, c) = Start();
        c.At(S(0), target: true);

        Assert.Equal(OffPlanVerdict.OnPlan, c.At(S(1), target: false));
        Assert.Equal(OffPlanVerdict.OnPlan, c.At(S(8.5), target: false));
        Assert.Equal(OffPlanVerdict.OffPlan, c.At(S(9.25), target: false));
    }

    [Fact]
    public void A_target_that_comes_back_quickly_is_fine()
    {
        var (_, c) = Start();
        c.At(S(0), target: true);
        c.At(S(1), target: false); // page loading, or a short scroll

        Assert.Equal(OffPlanVerdict.OnPlan, c.At(S(3), target: true));
        Assert.Equal(OffPlanVerdict.OnPlan, c.At(S(12), target: true));
    }

    [Fact]
    public void A_target_never_seen_is_not_lost_it_is_on_a_page_still_to_come()
    {
        var (_, c) = Start();

        for (var s = 0; s <= 20; s++)
            Assert.Equal(OffPlanVerdict.OnPlan, c.At(S(s), title: $"page {s % 2}", target: false));
    }

    // ---- stuck ----

    [Fact]
    public void Stuck_after_forty_seconds_with_a_ring_on_screen()
    {
        var (_, c) = Start();
        c.At(S(0));

        Assert.Equal(OffPlanVerdict.OnPlan, c.At(S(39)));
        Assert.Equal(OffPlanVerdict.Stuck, c.At(S(40)));
    }

    [Fact]
    public void Stuck_sooner_when_there_is_nothing_to_point_at()
    {
        var (_, c) = Start(KeysStep);
        c.At(S(0), target: null);

        Assert.Equal(OffPlanVerdict.OnPlan, c.At(S(24), target: null));
        Assert.Equal(OffPlanVerdict.Stuck, c.At(S(25), target: null));
    }

    [Fact]
    public void Typing_steps_get_two_minutes()
    {
        var (_, c) = Start(TypeStep);
        c.At(S(0));

        Assert.Equal(OffPlanVerdict.OnPlan, c.At(S(119)));
        Assert.Equal(OffPlanVerdict.Stuck, c.At(S(120)));
    }

    [Fact]
    public void Moving_between_windows_counts_as_trying_and_restarts_the_clock()
    {
        var (_, c) = Start();
        c.At(S(0));
        c.At(S(30), title: "Settings - Bluetooth");

        Assert.Equal(OffPlanVerdict.OnPlan, c.At(S(60), title: "Settings - Bluetooth"));
        Assert.Equal(OffPlanVerdict.Stuck, c.At(S(70), title: "Settings - Bluetooth"));
    }

    // ---- budget ----

    [Fact]
    public void Replans_are_at_least_twenty_seconds_apart()
    {
        var (detector, c) = Start();
        c.At(S(0));
        Assert.Equal(OffPlanVerdict.OffPlan, Away(c, S(1)));

        detector.Reset(c.Now); // the session starts the new guide
        c.StepStart = c.Now;
        c.At(c.Now - T0);
        Assert.Equal(OffPlanVerdict.OnPlan, Away(c, S(6), handle: 9));
    }

    [Fact]
    public void At_most_four_replans_in_ten_minutes()
    {
        var (detector, c) = Start();
        var verdicts = new List<OffPlanVerdict>();
        var t = S(0);

        for (var i = 0; i < 6; i++)
        {
            detector.Reset(T0 + t);
            c.StepStart = T0 + t;
            c.At(t);
            verdicts.Add(Away(c, t + S(1), handle: 100 + i));
            t += S(25);
        }

        Assert.Equal(4, verdicts.Count(v => v == OffPlanVerdict.OffPlan));
        Assert.Equal(OffPlanVerdict.OnPlan, verdicts.Last());
    }

    [Fact]
    public void The_budget_survives_reset_but_per_step_state_does_not()
    {
        var (detector, c) = Start();
        c.At(S(0), target: true);
        c.At(S(1), target: false);

        detector.Reset(T0 + S(2)); // next step: the old target no longer matters
        c.StepStart = T0 + S(2);

        Assert.Equal(OffPlanVerdict.OnPlan, c.At(S(12), target: false));
    }

    [Fact]
    public void LastReason_names_why_the_last_verdict_fired()
    {
        var (_, c) = Start();
        Assert.Null(c.Detector().LastReason);
        c.At(S(0));
        Assert.Equal(OffPlanVerdict.OffPlan, Away(c, S(1)));
        Assert.Equal("other-app", c.Detector().LastReason);
    }

    [Fact]
    public void LastReason_target_lost_and_stuck()
    {
        var (_, lost) = Start();
        lost.At(S(0), target: true);
        lost.At(S(1), target: false);
        Assert.Equal(OffPlanVerdict.OffPlan, lost.At(S(9.25), target: false));
        Assert.Equal("target-lost", lost.Detector().LastReason);

        var (_, stuck) = Start();
        stuck.At(S(0));
        Assert.Equal(OffPlanVerdict.Stuck, stuck.At(S(40)));
        Assert.Equal("stuck", stuck.Detector().LastReason);
    }

    [Fact]
    public void LastReason_is_not_set_by_a_verdict_the_budget_suppressed()
    {
        var (detector, c) = Start();
        c.At(S(0));
        Away(c, S(1));
        detector.Reset(c.Now);
        c.StepStart = c.Now;
        c.At(c.Now - T0);
        Away(c, S(6), handle: 9); // too soon after the last replan: suppressed

        Assert.Equal("other-app", detector.LastReason);
    }

    // Puts another app in front long enough to count, from the given time.
    private static OffPlanVerdict Away(Clock c, TimeSpan from, long handle = 7)
    {
        c.At(from, "chrome", "News", handle);
        return c.At(from + Ms(1750), "chrome", "News", handle);
    }
}

using Ashok.Core.Contracts;
using Ashok.Core.Evaluation;
using Ashok.Core.Session;
using static Ashok.Core.Tests.Steps;

namespace Ashok.Core.Tests;

public class TuneOffPlanTests
{
    private const string Q = "do the thing";

    private static UiElement Button(string name) => new("e1", "Button", name, null, [1, 2, 3, 4], true, null);

    private static CapturedScreen Screen(string process, long handle, params UiElement[] elements) =>
        new(new ForegroundState(process, process, handle), elements);

    private sealed class Log : IDebugLog
    {
        public List<(OffPlanVerdict Verdict, string Step, string Action)> Verdicts { get; } = [];

        public void Routed(DateTimeOffset at, string? process, string? playbookId, double? score, string path)
        {
        }

        public void Verdict(DateTimeOffset at, OffPlanVerdict verdict, string? reason, string process, string stepId, double secondsIntoStep, string action = "replan") =>
            Verdicts.Add((verdict, stepId, action));

        public void AutomationReset(DateTimeOffset at, string process, int hresult)
        {
        }
    }

    private static Rig NewRig(Playbook book, Log? log = null) =>
        new([book], new FakeMatcher((Q, book.Id)), detector: new HeuristicOffPlanDetector(), debugLog: log);

    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    // ---- a window that opens right after a completed step ----

    private static Playbook TwoSteps() => Book("x",
        Step("s1", Visible("Button", "Open")),
        Step("s2", Visible("Button", "Never"), new Target(null, "Button", "Go", null)));

    private static async Task<Rig> AtStepTwo()
    {
        var rig = NewRig(TwoSteps());
        await rig.Session.AskAsync(Q, 0);
        await rig.Tick(Screen("explorer", 1, Button("Open"))); // step 1 completes in explorer
        Assert.Equal(1, rig.Session.State.StepIndex);
        return rig;
    }

    [Fact]
    public async Task A_window_that_opens_shortly_after_a_completed_step_is_its_result_not_off_plan()
    {
        var rig = await AtStepTwo();

        rig.Time.Advance(S(3));
        await rig.Tick(Screen("SystemSettings", 2));
        rig.Time.Advance(S(1.6)); // long enough that an unexpected app would count
        await rig.Tick(Screen("SystemSettings", 2));

        Assert.Empty(rig.Planner.Requests);
        Assert.Equal(SessionPhase.Guiding, rig.Session.State.Phase);
    }

    [Fact]
    public async Task A_different_app_fifteen_seconds_into_a_step_is_still_off_plan()
    {
        var rig = await AtStepTwo();

        rig.Time.Advance(S(15));
        await rig.Tick(Screen("chrome", 7));
        rig.Time.Advance(S(1.6));
        await rig.Tick(Screen("chrome", 7));

        var request = Assert.Single(rig.Planner.Requests);
        Assert.Equal(PlanReason.OffPlan, request.Reason);
    }

    // ---- nudge before replanning ----

    private static Playbook WithRing() => Book("x", Step("s1", Visible("Button", "Never"), new Target(null, "Button", "Go", null), say: "Click Go."));

    [Fact]
    public async Task The_first_stuck_with_a_ring_on_screen_re_announces_gently_instead_of_replanning()
    {
        var log = new Log();
        var rig = NewRig(WithRing(), log);
        await rig.Session.AskAsync(Q, 0);
        await rig.Tick(Screen("SystemSettings", 1, Button("Go")));
        var announced = rig.View.Steps.Count;

        rig.Time.Advance(S(41));
        await rig.Tick(Screen("SystemSettings", 1, Button("Go")));

        Assert.Empty(rig.Planner.Requests);
        Assert.Equal(SessionPhase.Guiding, rig.Session.State.Phase);
        Assert.Equal(announced + 1, rig.View.Steps.Count);
        Assert.Equal("No rush. Click Go.", rig.View.Steps[^1].Say);
        Assert.Equal("No rush. Click Go.", rig.Speaker.Spoken[^1]);
        Assert.Equal((OffPlanVerdict.Stuck, "s1", "nudge"), Assert.Single(log.Verdicts));
    }

    [Fact]
    public async Task A_second_stuck_on_the_same_step_replans_as_before()
    {
        var log = new Log();
        var rig = NewRig(WithRing(), log);
        await rig.Session.AskAsync(Q, 0);
        await rig.Tick(Screen("SystemSettings", 1, Button("Go")));
        rig.Time.Advance(S(41));
        await rig.Tick(Screen("SystemSettings", 1, Button("Go"))); // nudge

        rig.Time.Advance(S(30));
        await rig.Tick(Screen("SystemSettings", 1, Button("Go")));
        Assert.Empty(rig.Planner.Requests); // the nudge restarted the wait

        rig.Time.Advance(S(11));
        await rig.Tick(Screen("SystemSettings", 1, Button("Go")));

        Assert.Equal(PlanReason.Stuck, Assert.Single(rig.Planner.Requests).Reason);
        Assert.Equal([("nudge"), ("replan")], log.Verdicts.Select(v => v.Action));
    }

    [Fact]
    public async Task Stuck_with_nothing_to_point_at_replans_without_a_nudge()
    {
        var log = new Log();
        var rig = NewRig(WithRing(), log);
        await rig.Session.AskAsync(Q, 0);
        await rig.Tick(Screen("SystemSettings", 1)); // the target is not on this screen

        rig.Time.Advance(S(26));
        await rig.Tick(Screen("SystemSettings", 1));

        Assert.Equal(PlanReason.Stuck, Assert.Single(rig.Planner.Requests).Reason);
        Assert.Equal("replan", Assert.Single(log.Verdicts).Action);
    }

    [Fact]
    public async Task Nudges_do_not_use_the_replan_budget_five_nudge_then_replan_cycles_still_allow_four_replans()
    {
        var log = new Log();
        var rig = NewRig(WithRing(), log);
        rig.Planner.Handler = (_, _) => Task.FromResult(new PlannerResult(
            new Plan("again", [Step("p1", Visible("Button", "Never"), new Target(null, "Button", "Go", null), say: "Click Go.")], 1, false, null), null));
        await rig.Session.AskAsync(Q, 0);

        for (var cycle = 0; cycle < 5; cycle++)
        {
            await rig.Tick(Screen("SystemSettings", 1, Button("Go")));
            rig.Time.Advance(S(41));
            await rig.Tick(Screen("SystemSettings", 1, Button("Go"))); // nudge
            rig.Time.Advance(S(41));
            await rig.Tick(Screen("SystemSettings", 1, Button("Go"))); // replan, unless the budget is spent
        }

        Assert.Equal(4, rig.Planner.Requests.Count);
        Assert.Equal(4, log.Verdicts.Count(v => v.Action == "replan"));
        // The fifth cycle's Stuck is held back by the spent budget before the session sees it, so it gets no nudge either.
        Assert.Equal(4, log.Verdicts.Count(v => v.Action == "nudge"));
    }

    [Fact]
    public async Task A_new_step_can_be_nudged_again()
    {
        var book = Book("x",
            Step("s1", Visible("Button", "Next"), new Target(null, "Button", "Go", null), say: "Click Go."),
            Step("s2", Visible("Button", "Never"), new Target(null, "Button", "Next", null), say: "Click Next."));
        var log = new Log();
        var rig = NewRig(book, log);
        await rig.Session.AskAsync(Q, 0);
        await rig.Tick(Screen("SystemSettings", 1, Button("Go")));
        rig.Time.Advance(S(41));
        await rig.Tick(Screen("SystemSettings", 1, Button("Go"))); // nudge on s1

        await rig.Tick(Screen("SystemSettings", 1, Button("Next"))); // s1 done, s2 begins
        rig.Time.Advance(S(41));
        await rig.Tick(Screen("SystemSettings", 1, Button("Next")));

        Assert.Empty(rig.Planner.Requests);
        Assert.Equal([("s1", "nudge"), ("s2", "nudge")], log.Verdicts.Select(v => (v.Step, v.Action)));
    }
}

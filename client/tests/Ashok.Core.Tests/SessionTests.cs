using Ashok.Core.Contracts;
using Ashok.Core.Overlay;
using Ashok.Core.Session;
using static Ashok.Core.Tests.Steps;

namespace Ashok.Core.Tests;

public class SessionTests
{
    private static UiElement Button(string name, int x = 100, int y = 200) => Els.El("Button", name, x: x, y: y);

    private static (Rig Rig, FakeMatcher Matcher) TwoStepRig()
    {
        var book = Book("turn-on",
            Step("s1", Visible("Button", "Bluetooth"), new Target(null, "ListItem", "Bluetooth & devices", null), say: "Click Bluetooth & devices."),
            Step("s2", Visible("Text", "Bluetooth is on"), new Target(null, "Button", "Bluetooth", null), say: "Turn Bluetooth on."));
        var matcher = new FakeMatcher(("bluetooth", "turn-on"));
        return (new Rig([book], matcher), matcher);
    }

    // ---- Ask / Matching ----

    [Fact]
    public async Task A_matched_question_starts_the_playbook_and_announces_step_one()
    {
        var (rig, _) = TwoStepRig();

        await rig.Session.AskAsync("bluetooth", 42);

        Assert.Equal(new SessionState(SessionPhase.Guiding, "turn-on", 0), rig.Session.State);
        Assert.Equal([SessionPhase.Matching, SessionPhase.Guiding], rig.States.Select(s => s.Phase));
        Assert.Equal("Click Bluetooth & devices.", Assert.Single(rig.View.Steps).Say);
        Assert.Equal(["Click Bluetooth & devices."], rig.Speaker.Spoken);
        Assert.Empty(rig.Planner.Requests);
    }

    [Fact]
    public async Task No_match_and_no_planner_gives_up_kindly()
    {
        var rig = new Rig([Book("x", Step("s1", Confirm()))]);

        await rig.Session.AskAsync("something unrelated", 0);

        Assert.Equal(SessionPhase.GaveUp, rig.Session.State.Phase);
        Assert.Equal([SessionPhase.Matching, SessionPhase.Replanning, SessionPhase.GaveUp], rig.States.Select(s => s.Phase));
        Assert.Equal(GuidanceSession.NotSureMessage, Assert.Single(rig.View.Messages));
        Assert.Equal(PlanReason.New, Assert.Single(rig.Planner.Requests).Reason);
    }

    [Fact]
    public async Task No_match_uses_the_plan_the_planner_returns()
    {
        var rig = new Rig([Book("x", Step("s1", Confirm()))]);
        rig.Planner.Handler = (_, _) => Task.FromResult(new PlannerResult(
            new Plan("night_light", [Step("p1", Confirm(), say: "Planned step.")], 0.8, false, null), null));

        await rig.Session.AskAsync("turn on night light", 0);

        Assert.Equal(new SessionState(SessionPhase.Guiding, "night_light", 0), rig.Session.State);
        Assert.Equal("Planned step.", rig.View.Steps.Single().Say);
    }

    [Fact]
    public async Task A_refusal_is_shown_as_the_message()
    {
        var rig = new Rig([Book("x", Step("s1", Confirm()))]);
        rig.Planner.Handler = (_, _) => Task.FromResult(new PlannerResult(
            new Plan("refused", [], 1, false, "Please call a family member for this one."), null));

        await rig.Session.AskAsync("buy gift cards", 0);

        Assert.Equal(SessionPhase.GaveUp, rig.Session.State.Phase);
        Assert.Equal("Please call a family member for this one.", Assert.Single(rig.View.Messages));
    }

    [Fact]
    public async Task A_planner_that_throws_is_treated_as_unavailable()
    {
        var rig = new Rig([Book("x", Step("s1", Confirm()))]);
        rig.Planner.Handler = (_, _) => throw new InvalidOperationException("boom");

        await rig.Session.AskAsync("anything", 0);

        Assert.Equal(SessionPhase.GaveUp, rig.Session.State.Phase);
    }

    // ---- Guiding ----

    [Fact]
    public async Task A_tick_that_satisfies_the_step_advances_and_announces_the_next()
    {
        var (rig, _) = TwoStepRig();
        await rig.Session.AskAsync("bluetooth", 0);

        await rig.Tick(Rig.Screen(elements: [Button("Bluetooth")]));

        Assert.Equal(1, rig.Session.State.StepIndex);
        Assert.Equal(["Click Bluetooth & devices.", "Turn Bluetooth on."], rig.View.Steps.Select(s => s.Say));
        Assert.Equal(1, rig.View.Steps[1].Index);
        Assert.Equal(2, rig.View.Steps[1].Total);
    }

    [Fact]
    public async Task Finishing_the_last_step_ends_in_Done_with_a_message()
    {
        var (rig, _) = TwoStepRig();
        await rig.Session.AskAsync("bluetooth", 0);
        await rig.Tick(Rig.Screen(elements: [Button("Bluetooth")]));

        await rig.Tick(Rig.Screen(elements: [Els.El("Text", "Bluetooth is on")]));

        Assert.Equal(SessionPhase.Done, rig.Session.State.Phase);
        Assert.Equal(GuidanceSession.DoneMessage, rig.View.Messages.Last());
        Assert.Equal("hide", rig.Overlay.Calls.Last());
    }

    [Fact]
    public async Task A_tick_that_changes_nothing_keeps_the_state_and_does_not_re_announce()
    {
        var (rig, _) = TwoStepRig();
        await rig.Session.AskAsync("bluetooth", 0);
        var before = rig.States.Count;

        await rig.Tick(Rig.Screen(elements: [Button("Printers")]));
        await rig.Tick(Rig.Screen(elements: [Button("Printers")]));

        Assert.Equal(before, rig.States.Count);
        Assert.Single(rig.View.Steps);
        Assert.Single(rig.Speaker.Spoken);
    }

    [Fact]
    public async Task The_ring_follows_the_target_and_is_not_redrawn_when_it_has_not_moved()
    {
        var (rig, _) = TwoStepRig();
        await rig.Session.AskAsync("bluetooth", 0);
        var nav = Els.El("ListItem", "Bluetooth & devices", x: 24, y: 310);

        await rig.Tick(Rig.Screen(elements: [nav]));
        await rig.Tick(Rig.Screen(elements: [nav]));
        Assert.Equal([new ScreenRect(24, 310, 100, 30)], rig.Overlay.Shown);

        var moved = Els.El("ListItem", "Bluetooth & devices", x: 24, y: 400);
        await rig.Tick(Rig.Screen(elements: [moved]));
        Assert.Equal(2, rig.Overlay.Shown.Count);

        await rig.Tick(Rig.Screen(elements: []));
        Assert.Equal("hide", rig.Overlay.Calls.Last());
    }

    [Fact]
    public async Task A_step_without_a_target_shows_no_ring()
    {
        var book = Book("keys", Step("s1", Opened("explorer"), target: null, action: ActionKind.PressKeys));
        var rig = new Rig([book], new FakeMatcher(("files", "keys")));
        await rig.Session.AskAsync("files", 0);

        await rig.Tick(Rig.Screen("chrome", "Tab", 1));

        Assert.Empty(rig.Overlay.Shown);
    }

    [Fact]
    public async Task Steps_the_user_already_finished_are_skipped_and_only_the_landing_step_is_announced()
    {
        var book = Book("three",
            Step("s1", Visible("Button", "A"), say: "One."),
            Step("s2", Visible("Button", "B"), say: "Two."),
            Step("s3", Visible("Button", "C"), say: "Three."));
        var rig = new Rig([book], new FakeMatcher(("go", "three")));
        await rig.Session.AskAsync("go", 0);

        await rig.Tick(Rig.Screen(elements: [Button("A"), Button("B")]));

        Assert.Equal(2, rig.Session.State.StepIndex);
        Assert.Equal(["One.", "Three."], rig.View.Steps.Select(s => s.Say));
    }

    [Fact]
    public async Task WindowOpened_is_not_satisfied_by_a_window_that_was_already_in_front_when_the_step_began()
    {
        var book = Book("open", Step("s1", Opened("explorer"), action: ActionKind.PressKeys), Step("s2", Confirm()));
        var rig = new Rig([book], new FakeMatcher(("files", "open")));
        await rig.Session.AskAsync("files", 0);

        await rig.Tick(Rig.Screen("explorer", "Home", 7));
        Assert.Equal(0, rig.Session.State.StepIndex);

        await rig.Tick(Rig.Screen("explorer", "Downloads", 8)); // a new window of the same program
        Assert.Equal(1, rig.Session.State.StepIndex);
    }

    [Fact]
    public async Task ElementGone_waits_until_the_element_has_been_seen_then_disappears()
    {
        var book = Book("close", Step("s1", Gone("Button", "OK")), Step("s2", Confirm()));
        var rig = new Rig([book], new FakeMatcher(("close it", "close")));
        await rig.Session.AskAsync("close it", 0);

        await rig.Tick(Rig.Screen(elements: []));
        Assert.Equal(0, rig.Session.State.StepIndex);

        await rig.Tick(Rig.Screen(elements: [Button("OK")]));
        Assert.Equal(0, rig.Session.State.StepIndex);

        await rig.Tick(Rig.Screen(elements: []));
        Assert.Equal(1, rig.Session.State.StepIndex);
    }

    // ---- Confirm ----

    [Fact]
    public async Task Confirm_only_works_on_a_userConfirms_step()
    {
        var book = Book("c", Step("s1", Visible("Button", "A")), Step("s2", Confirm()));
        var rig = new Rig([book], new FakeMatcher(("c", "c")));
        await rig.Session.AskAsync("c", 0);

        Assert.False(rig.Session.Confirm());

        await rig.Tick(Rig.Screen(elements: [Button("A")]));
        Assert.True(rig.Session.Confirm());
        Assert.True(rig.View.Steps.Last().NeedsConfirm);

        await rig.Tick(Rig.Screen());
        Assert.Equal(SessionPhase.Done, rig.Session.State.Phase);
    }

    [Fact]
    public void Confirm_outside_a_session_does_nothing() =>
        Assert.False(new Rig([Book("x", Step("s1", Confirm()))]).Session.Confirm());

    // ---- Cancel / new ask ----

    [Fact]
    public async Task Cancel_returns_to_Idle_hides_the_ring_and_silences_speech()
    {
        var (rig, _) = TwoStepRig();
        await rig.Session.AskAsync("bluetooth", 0);
        await rig.Tick(Rig.Screen(elements: [Els.El("ListItem", "Bluetooth & devices")]));

        rig.Session.Cancel();

        Assert.Equal(SessionPhase.Idle, rig.Session.State.Phase);
        Assert.Equal("hide", rig.Overlay.Calls.Last());
        Assert.True(rig.Speaker.Stops > 0);
        Assert.True(rig.View.Clears > 0);
    }

    [Fact]
    public async Task Ticks_after_a_cancel_are_ignored()
    {
        var (rig, _) = TwoStepRig();
        await rig.Session.AskAsync("bluetooth", 0);
        rig.Session.Cancel();

        await rig.Tick(Rig.Screen(elements: [Button("Bluetooth")]));

        Assert.Equal(SessionPhase.Idle, rig.Session.State.Phase);
    }

    [Fact]
    public async Task A_planner_answer_that_arrives_after_a_cancel_is_discarded()
    {
        var rig = new Rig([Book("x", Step("s1", Confirm()))]);
        var release = new TaskCompletionSource<PlannerResult>();
        rig.Planner.Handler = (_, _) => release.Task;

        var ask = rig.Session.AskAsync("slow question", 0);
        Assert.Equal(SessionPhase.Replanning, rig.Session.State.Phase);
        rig.Session.Cancel();
        release.SetResult(new PlannerResult(new Plan("late", [Step("p1", Confirm())], 1, false, null), null));
        await ask;

        Assert.Equal(SessionPhase.Idle, rig.Session.State.Phase);
        Assert.Empty(rig.View.Steps);
    }

    [Fact]
    public async Task Asking_again_replaces_the_current_guide()
    {
        var other = Book("other", Step("o1", Confirm(), say: "Other."));
        var book = Book("turn-on", Step("s1", Visible("Button", "Bluetooth"), say: "First."));
        var rig = new Rig([book, other], new FakeMatcher(("a", "turn-on"), ("b", "other")));
        await rig.Session.AskAsync("a", 0);

        await rig.Session.AskAsync("b", 0);

        Assert.Equal(new SessionState(SessionPhase.Guiding, "other", 0), rig.Session.State);
        Assert.Equal("Other.", rig.View.Steps.Last().Say);
    }

    // ---- Off plan / stuck ----

    private static async Task<Rig> GuidingRig(IPlanner? planner = null)
    {
        var book = Book("g", Step("s1", Visible("Button", "Never")), Step("s2", Confirm()));
        var rig = new Rig([book], new FakeMatcher(("go", "g")), planner);
        await rig.Session.AskAsync("go", 0);
        await rig.Tick(Rig.Screen("SystemSettings", "Settings", 1)); // first tick learns the starting window
        return rig;
    }

    [Fact]
    public async Task An_unexpected_window_that_stays_in_front_replans_as_off_plan_within_two_seconds()
    {
        var rig = await GuidingRig();
        var tick = TimeSpan.FromMilliseconds(250);
        var startedAt = rig.Time.GetUtcNow();
        TimeSpan? detectedAfter = null;

        for (var i = 0; i < 12 && detectedAfter is null; i++)
        {
            rig.Time.Advance(tick);
            await rig.Tick(Rig.Screen("MysteryApp", "Surprise", 9));
            if (rig.Planner.Requests.Count > 0) detectedAfter = rig.Time.GetUtcNow() - startedAt;
        }

        Assert.NotNull(detectedAfter);
        Assert.True(detectedAfter <= TimeSpan.FromSeconds(2), $"detected after {detectedAfter}");
        Assert.Equal(PlanReason.OffPlan, rig.Planner.Requests.Single().Reason);
        Assert.Equal("MysteryApp", rig.Planner.Requests.Single().Foreground!.Process);
    }

    [Fact]
    public async Task A_flicker_of_an_unexpected_window_is_not_off_plan()
    {
        var rig = await GuidingRig();

        rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        await rig.Tick(Rig.Screen("MysteryApp", "x", 9));
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        await rig.Tick(Rig.Screen("SystemSettings", "Settings", 1));
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        await rig.Tick(Rig.Screen("SystemSettings", "Settings", 1));

        Assert.Empty(rig.Planner.Requests);
        Assert.Equal(SessionPhase.Guiding, rig.Session.State.Phase);
    }

    [Fact]
    public async Task Forty_five_seconds_without_progress_replans_as_stuck()
    {
        var rig = await GuidingRig();

        rig.Time.Advance(TimeSpan.FromSeconds(44));
        await rig.Tick(Rig.Screen("SystemSettings", "Settings", 1));
        Assert.Empty(rig.Planner.Requests);

        rig.Time.Advance(TimeSpan.FromSeconds(2));
        await rig.Tick(Rig.Screen("SystemSettings", "Settings", 1));

        Assert.Equal(PlanReason.Stuck, Assert.Single(rig.Planner.Requests).Reason);
    }

    [Fact]
    public async Task Ashoks_own_window_in_front_is_ignored()
    {
        var rig = await GuidingRig();

        for (var i = 0; i < 10; i++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(1));
            await rig.Tick(Rig.Screen(Rig.Own, "Ashok", 5, Button("Never")));
        }

        Assert.Empty(rig.Planner.Requests);
        Assert.Equal(0, rig.Session.State.StepIndex); // its "Never" button did not satisfy step one
    }

    [Fact]
    public async Task The_planner_gets_the_history_and_a_new_plan_resumes_guiding()
    {
        var book = Book("g", Step("s1", Visible("Button", "A"), say: "One."), Step("s2", Visible("Button", "Never")));
        var rig = new Rig([book], new FakeMatcher(("go", "g")));
        rig.Planner.Handler = (_, _) => Task.FromResult(new PlannerResult(
            new Plan("recovery", [Step("r1", Confirm(), say: "Recovery step.")], 0.7, false, null), null));
        await rig.Session.AskAsync("go", 0);
        await rig.Tick(Rig.Screen("SystemSettings", "Settings", 1, Button("A")));

        rig.Time.Advance(TimeSpan.FromSeconds(46));
        await rig.Tick(Rig.Screen("SystemSettings", "Settings", 1));

        var request = Assert.Single(rig.Planner.Requests);
        Assert.Equal("go", request.Question);
        Assert.Equal(["s1"], request.History.Select(h => h.StepId));
        Assert.Equal(new SessionState(SessionPhase.Guiding, "recovery", 0), rig.Session.State);
        Assert.Equal("Recovery step.", rig.View.Steps.Last().Say);
    }

    [Fact]
    public async Task With_no_planner_an_off_plan_session_gives_up_gently()
    {
        var rig = await GuidingRig();

        rig.Time.Advance(TimeSpan.FromSeconds(46));
        await rig.Tick(Rig.Screen("SystemSettings", "Settings", 1));

        Assert.Equal(SessionPhase.GaveUp, rig.Session.State.Phase);
        Assert.Equal(GuidanceSession.LostMessage, rig.View.Messages.Last());
    }

    // ---- Offline guarantee ----

    [Fact]
    public async Task A_playbook_session_never_calls_the_planner()
    {
        var tripwire = new TripwirePlanner();
        var book = Book("g", Step("s1", Visible("Button", "A")), Step("s2", Confirm()));
        var rig = new Rig([book], new FakeMatcher(("go", "g")), tripwire);

        await rig.Session.AskAsync("go", 0);
        await rig.Tick(Rig.Screen(elements: [Button("A")]));
        rig.Session.Confirm();
        await rig.Tick(Rig.Screen());

        Assert.Equal(SessionPhase.Done, rig.Session.State.Phase);
        Assert.Equal(0, tripwire.Calls);
    }
}

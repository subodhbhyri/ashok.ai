using Ashok.Core.Contracts;
using Ashok.Core.Evaluation;
using Ashok.Core.Intent;
using Ashok.Core.Playbooks;
using Ashok.Core.Resolution;
using Ashok.Core.Session;
using static Ashok.Core.Tests.Steps;

namespace Ashok.Core.Tests;

public class PlaybookFixesTests
{
    private static UiElement El(string role, string name, string? id = null, int x = 10) =>
        new("e1", role, name, id, [x, 20, 100, 30], true, null);

    // ---- opposite intent ----

    private static Playbook Bluetooth(params string[] avoid) =>
        Book("bluetooth-on", Step("s1", Confirm())) with
        {
            Examples = ["turn on bluetooth", "enable bluetooth"],
            AvoidWords = avoid.Length == 0 ? null : avoid,
        };

    private static Playbook Pair() =>
        Book("pair-device", Step("s1", Confirm())) with { Examples = ["pair a bluetooth device", "connect my headphones"] };

    private static EmbeddingIntentMatcher Matcher(params Playbook[] books) => new(new HashingEmbedder(), books, threshold: 0.2, margin: 0);

    [Fact]
    public void A_question_with_an_avoid_word_skips_that_playbook()
    {
        var matcher = Matcher(Bluetooth("off", "disable", "disconnect", "stop"));

        Assert.Equal("bluetooth-on", matcher.Match("how do I turn on bluetooth")?.PlaybookId);
        Assert.Null(matcher.Match("how do I turn off bluetooth"));
        Assert.DoesNotContain(matcher.Rank("how do I turn off bluetooth"), s => s.PlaybookId == "bluetooth-on");
    }

    [Fact]
    public void Avoid_words_are_whole_words_and_case_insensitive()
    {
        var matcher = Matcher(Bluetooth("off", "stop"));

        Assert.Null(matcher.Match("TURN OFF bluetooth"));
        Assert.Null(matcher.Match("stop bluetooth, please"));
        Assert.Equal("bluetooth-on", matcher.Match("turn on bluetooth offline mode")?.PlaybookId); // "offline" is not "off"
    }

    [Fact]
    public void The_next_best_playbook_is_used_when_the_first_is_avoided()
    {
        var matcher = Matcher(Bluetooth("off"), Pair());

        var match = matcher.Match("turn off bluetooth device");

        Assert.NotEqual("bluetooth-on", match?.PlaybookId);
    }

    [Fact]
    public async Task How_do_I_turn_off_bluetooth_goes_to_the_planner()
    {
        var book = Bluetooth("off");
        var session = RigWith(Matcher(book), book, out var planner);

        await session.AskAsync("how do I turn off bluetooth", 0);

        var request = Assert.Single(planner.Requests);
        Assert.Equal(PlanReason.New, request.Reason);
        Assert.Null(request.MatchedPlaybookId);
    }

    [Fact]
    public async Task How_do_I_turn_on_bluetooth_still_uses_the_playbook()
    {
        var book = Bluetooth("off");
        var session = RigWith(Matcher(book), book, out var planner);

        await session.AskAsync("how do I turn on bluetooth", 0);

        Assert.Empty(planner.Requests);
        Assert.Equal(SessionPhase.Guiding, session.State.Phase);
    }

    private static GuidanceSession RigWith(IIntentMatcher matcher, Playbook book, out FakePlanner planner)
    {
        var resolver = new TargetResolver();
        planner = new FakePlanner();
        return new GuidanceSession(matcher, new PlaybookStore([book]), planner, new NaiveOffPlanDetector(), resolver,
            new DoneEvaluator(resolver), new FakeOverlay(), new FakeSpeaker(), new FakeView(), new InlineDispatcher(),
            new FakeTime(), Rig.Own);
    }

    [Fact]
    public void The_loader_reads_avoidWords_and_rejects_bad_ones()
    {
        const string yaml = "id: x\ntitle: X\nscope: system\nAVOIDos: [win11]\nexamples: [a]\nsteps:\n  - id: s1\n    say: Look.\n    action: observe\n    doneWhen: { kind: userConfirms }\n";
        string With(string avoid) => yaml.Replace("AVOID", avoid);

        Assert.Equal(["off", "stop"], PlaybookLoader.Parse(With("avoidWords: [off, stop]\n"), "x").AvoidWords);
        Assert.Null(PlaybookLoader.Parse(With(""), "x").AvoidWords);
        Assert.Throws<PlaybookException>(() => PlaybookLoader.Parse(With("avoidWords: []\n"), "x"));
        Assert.Throws<PlaybookException>(() => PlaybookLoader.Parse(With("avoidWords: [\"turn off\"]\n"), "x"));
    }

    // ---- already done ----

    private static Playbook ThreeSteps() => Book("x",
        Step("s1", Visible("Button", "One")),
        Step("s2", Visible("Button", "Two")),
        Step("s3", Visible("Button", "Three")));

    [Fact]
    public async Task Completing_the_last_step_and_an_earlier_one_in_a_single_tick_says_it_was_already_done()
    {
        var rig = new Rig([ThreeSteps()], new FakeMatcher(("go", "x")));
        await rig.Session.AskAsync("go", 0);

        await rig.Tick(Rig.Screen(elements: [El("Button", "One"), El("Button", "Two"), El("Button", "Three")]));

        Assert.Equal(SessionPhase.Done, rig.Session.State.Phase);
        Assert.Equal(GuidanceSession.AlreadyDoneMessage, rig.View.Messages.Last());
        Assert.Equal("It looks like that's already done.", rig.Speaker.Spoken.Last());
    }

    [Fact]
    public async Task Finishing_the_last_step_on_its_own_still_says_all_done()
    {
        var rig = new Rig([ThreeSteps()], new FakeMatcher(("go", "x")));
        await rig.Session.AskAsync("go", 0);
        await rig.Tick(Rig.Screen(elements: [El("Button", "One"), El("Button", "Two")]));

        await rig.Tick(Rig.Screen(elements: [El("Button", "Three")]));

        Assert.Equal(GuidanceSession.DoneMessage, rig.View.Messages.Last());
    }

    [Fact]
    public async Task A_one_step_playbook_completed_in_one_tick_says_all_done()
    {
        var rig = new Rig([Book("x", Step("s1", Visible("Button", "One")))], new FakeMatcher(("go", "x")));
        await rig.Session.AskAsync("go", 0);

        await rig.Tick(Rig.Screen(elements: [El("Button", "One")]));

        Assert.Equal(GuidanceSession.DoneMessage, rig.View.Messages.Last());
    }

    // ---- rings only on certain matches ----

    private static readonly Target DisplayItem = new(null, "ListItem", "Display", null);

    [Fact]
    public void A_button_named_Display_is_a_role_fallback_for_a_ListItem_target_and_places_no_ring()
    {
        var resolver = new TargetResolver();
        var candidates = new[] { El("Button", "Display") };

        Assert.Equal(MatchKind.RoleFallback, resolver.Best(DisplayItem, candidates)!.How);
        Assert.Null(resolver.BestForRing(DisplayItem, candidates));
    }

    [Theory]
    [InlineData("ListItem", "Display", null, true)]     // exact role and name
    [InlineData("ListItem", "Displays", null, false)]   // fuzzy name
    [InlineData("Button", "Display", null, false)]      // compatible role only
    [InlineData("Button", "Something else", "DisplayId", true)] // automation id
    public void Only_automation_id_or_exact_role_and_name_place_a_ring(string role, string name, string? id, bool ring)
    {
        var target = id is null ? DisplayItem : DisplayItem with { AutomationId = "DisplayId" };

        Assert.Equal(ring, new TargetResolver().BestForRing(target, [El(role, name, id)]) is not null);
    }

    [Fact]
    public void A_pinned_suffix_still_counts_as_the_same_name()
    {
        var target = new Target(null, "TreeItem", "Downloads", null);

        Assert.NotNull(new TargetResolver().BestForRing(target, [El("TreeItem", "Downloads (pinned)")]));
    }

    [Fact]
    public async Task A_role_fallback_hides_the_ring_and_reports_the_target_as_not_on_screen()
    {
        var book = Book("x", Step("s1", Visible("Button", "Never"), new Target(null, "ListItem", "Display", null)));
        var rig = NewRig(book);
        await rig.Session.AskAsync("go", 0);

        await rig.Tick(Rig.Screen(elements: [El("Button", "Display")]));
        Assert.Empty(rig.Overlay.Shown);

        await rig.Tick(Rig.Screen(elements: [El("ListItem", "Display")]));
        Assert.Single(rig.Overlay.Shown);

        await rig.Tick(Rig.Screen(elements: [El("Button", "Display")]));
        Assert.Equal("hide", rig.Overlay.Calls.Last()); // the real target is gone: the ring must not jump to the look-alike
    }

    private static Rig NewRig(Playbook book) => new([book], new FakeMatcher(("go", book.Id)));
}

using Ashok.Core.Contracts;
using Ashok.Core.Evaluation;
using Ashok.Core.Resolution;

namespace Ashok.Core.Tests;

internal static class Els
{
    private static int _n;

    public static UiElement El(string role, string name, string? id = null, string? toggle = null, int x = 0, int y = 0) =>
        new($"e{Interlocked.Increment(ref _n)}", role, name, id, [x, y, 100, 30], true, toggle);

    public static Target T(string role, string name, string? id = null) => new(null, role, name, id);
}

public class TargetResolverTests
{
    private static readonly TargetResolver Resolver = new();

    [Fact]
    public void Automation_id_beats_everything()
    {
        var byId = Els.El("Button", "Totally different label", id: "SettingsPageGroupDevices", x: 500);
        var byName = Els.El("Button", "Bluetooth", x: 0);

        var best = Resolver.Best(Els.T("Button", "Bluetooth", "SettingsPageGroupDevices"), [byName, byId]);

        Assert.Equal(MatchKind.AutomationId, best!.How);
        Assert.Same(byId, best.Element);
    }

    [Fact]
    public void A_different_automation_id_rules_out_a_control_that_only_shares_the_name()
    {
        // The Network page's quick toggle is named "Wi-Fi" too, but it is not the Wi-Fi switch on the Wi-Fi page.
        var quickToggle = Els.El("Button", "Wi-Fi", id: "SystemSettings_Network_Wifi_QuickAction_ToggleSwitch");
        var wifiPageSwitch = Els.El("Button", "Wi-Fi", id: "SystemSettings_Connections_Adapter_Wi-Fi_Wi-Fi_RadioToggle", x: 300);
        var target = Els.T("Button", "Wi-Fi", "SystemSettings_Connections_Adapter_Wi-Fi_Wi-Fi_RadioToggle");

        Assert.Null(Resolver.Best(target, [quickToggle]));
        Assert.Same(wifiPageSwitch, Resolver.Best(target, [quickToggle, wifiPageSwitch])!.Element);
    }

    [Fact]
    public void An_element_with_no_id_can_still_match_a_target_that_names_one()
    {
        var plain = Els.El("Button", "Bluetooth");

        Assert.NotNull(Resolver.Best(Els.T("Button", "Bluetooth", "SomeId"), [plain]));
    }

    [Fact]
    public void Exact_name_is_case_and_punctuation_insensitive()
    {
        var wifi = Els.El("Button", "Wi-Fi");

        Assert.Equal(MatchKind.ExactName, Resolver.Best(Els.T("Button", "WIFI"), [wifi])!.How);
        Assert.Equal(MatchKind.ExactName, Resolver.Best(Els.T("Button", "  wi fi "), [Els.El("Button", "Wi Fi")])!.How);
    }

    [Fact]
    public void Ampersand_reads_as_and()
    {
        var item = Els.El("ListItem", "Bluetooth & devices");

        Assert.Equal(MatchKind.ExactName, Resolver.Best(Els.T("ListItem", "Bluetooth and devices"), [item])!.How);
    }

    [Fact]
    public void Small_typos_and_reordered_words_match_fuzzily()
    {
        Assert.Equal(MatchKind.FuzzyName, Resolver.Best(Els.T("Button", "Bluetoth"), [Els.El("Button", "Bluetooth")])!.How);
        Assert.NotNull(Resolver.Best(Els.T("Button", "Display settings"), [Els.El("Button", "Settings display")]));
    }

    [Fact]
    public void Explorers_pinned_and_start_of_group_names_still_match_the_plain_name()
    {
        // Real names from Windows 11 File Explorer's sidebar.
        var sidebar = Els.El("TreeItem", "Start of Quick Access - Downloads (pinned)");
        var desktop = Els.El("TreeItem", "Desktop (pinned)", y: 100);

        var best = Resolver.Best(Els.T("TreeItem", "Downloads"), [desktop, sidebar]);

        Assert.Same(sidebar, best!.Element);
        Assert.Equal(MatchKind.PartialName, best.How);
    }

    [Fact]
    public void A_true_exact_name_outranks_a_partial_one()
    {
        var partial = Els.El("TreeItem", "Downloads (pinned)");
        var exact = Els.El("TreeItem", "Downloads", y: 50);

        Assert.Same(exact, Resolver.Best(Els.T("TreeItem", "Downloads"), [partial, exact])!.Element);
    }

    [Fact]
    public void A_segment_match_needs_the_spaced_hyphen_so_Wi_Fi_is_unaffected()
    {
        Assert.Equal(MatchKind.ExactName, Resolver.Best(Els.T("Button", "Wi-Fi"), [Els.El("Button", "Wi-Fi")])!.How);
        Assert.Null(Resolver.Best(Els.T("Button", "Fi"), [Els.El("Button", "Wi-Fi")]));
    }

    [Fact]
    public void A_shorter_name_does_not_match_a_longer_one_that_merely_starts_with_it()
    {
        // "Bluetooth" must not be satisfied by the navigation item "Bluetooth & devices".
        var nav = Els.El("ListItem", "Bluetooth & devices");

        Assert.Null(Resolver.Best(Els.T("Button", "Bluetooth"), [nav]));
    }

    [Fact]
    public void Exact_match_outranks_a_fuzzy_one_regardless_of_order()
    {
        var fuzzy = Els.El("Button", "Bluetoot");
        var exact = Els.El("Button", "Bluetooth", x: 400);

        Assert.Same(exact, Resolver.Best(Els.T("Button", "Bluetooth"), [fuzzy, exact])!.Element);
    }

    [Fact]
    public void Compatible_roles_match_with_a_lower_score_than_the_exact_role()
    {
        var asListItem = Els.El("ListItem", "Downloads");
        var asTreeItem = Els.El("TreeItem", "Downloads", x: 300);

        var result = Resolver.Resolve(Els.T("TreeItem", "Downloads"), [asListItem, asTreeItem]);

        Assert.Same(asTreeItem, result.Best!.Element);
        Assert.Equal(MatchKind.RoleFallback, result.Matches[1].How);
        Assert.True(result.Matches[1].Score < result.Best.Score);
    }

    [Fact]
    public void Incompatible_roles_never_match()
    {
        Assert.Null(Resolver.Best(Els.T("Button", "Downloads"), [Els.El("Edit", "Downloads")]));
        Assert.Null(Resolver.Best(Els.T("Text", "Downloads"), [Els.El("Button", "Downloads")]));
    }

    [Fact]
    public void Nothing_below_the_threshold_is_returned() =>
        Assert.Empty(Resolver.Resolve(Els.T("Button", "Bluetooth"), [Els.El("Button", "Printers"), Els.El("Button", "Camera")]).Matches);

    [Fact]
    public void Identical_matches_are_reported_as_a_tie_in_document_order()
    {
        var first = Els.El("Button", "Connect", x: 0);
        var second = Els.El("Button", "Connect", x: 400);

        var result = Resolver.Resolve(Els.T("Button", "Connect"), [first, second]);

        Assert.True(result.IsTie);
        Assert.Same(first, result.Best!.Element);
    }

    [Fact]
    public void A_single_match_is_not_a_tie() =>
        Assert.False(Resolver.Resolve(Els.T("Button", "Connect"), [Els.El("Button", "Connect")]).IsTie);

    [Fact]
    public void Text_that_the_ranker_would_drop_can_still_be_found()
    {
        // The Downloads heading repeats the sidebar item's name; HeuristicRanker removes it, candidates keep it.
        var candidates = new[] { Els.El("TreeItem", "Downloads"), Els.El("Text", "Downloads", x: 400) };
        var ranked = new Ashok.Core.Capture.HeuristicRanker().Rank(candidates, 150).Elements;

        Assert.DoesNotContain(ranked, e => e.Role == "Text");
        Assert.NotNull(Resolver.Best(Els.T("Text", "Downloads"), candidates));
        Assert.Null(Resolver.Best(Els.T("Text", "Downloads"), ranked));
    }
}

public class DoneEvaluatorTests
{
    private static readonly DoneEvaluator Evaluator = new(new TargetResolver());

    private static readonly ForegroundState Explorer = new("explorer", "Home - File Explorer", 100);

    private static EvaluationContext Ctx(
        IReadOnlyList<UiElement>? candidates = null, ForegroundState? now = null, ForegroundState? start = null,
        FocusedElement? focus = null, bool confirmed = false, bool seen = false) =>
        new(candidates ?? [], now ?? Explorer, start, focus, confirmed, seen);

    private static DoneWhen Window(string? process = null, string? title = null) =>
        new(DoneKind.WindowOpened, process, title, null, null);

    private static DoneWhen Visible(Target t) => new(DoneKind.ElementVisible, null, null, t, null);

    // ---- windowOpened ----

    [Fact]
    public void WindowOpened_when_the_process_comes_to_the_front()
    {
        var start = new ForegroundState("chrome", "Tab", 1);

        Assert.True(Evaluator.IsDone(Window("explorer"), Ctx(now: Explorer, start: start)));
    }

    [Fact]
    public void WindowOpened_is_false_when_something_else_is_in_front() =>
        Assert.False(Evaluator.IsDone(Window("explorer"), Ctx(now: new ForegroundState("chrome", "x", 5))));

    [Fact]
    public void WindowOpened_ignores_exe_suffix_and_case() =>
        Assert.True(Evaluator.IsDone(Window("Explorer.EXE"), Ctx(now: Explorer, start: new ForegroundState("a", "b", 1))));

    [Fact]
    public void WindowOpened_is_false_when_the_same_window_was_already_in_front()
    {
        Assert.False(Evaluator.IsDone(Window("explorer"), Ctx(now: Explorer, start: Explorer)));
    }

    [Fact]
    public void WindowOpened_is_true_for_a_new_window_of_the_same_program()
    {
        var newer = Explorer with { Handle = 999 };

        Assert.True(Evaluator.IsDone(Window("explorer"), Ctx(now: newer, start: Explorer)));
    }

    [Fact]
    public void WindowOpened_can_match_on_title()
    {
        var settings = new ForegroundState("SystemSettings", "Settings", 3);

        Assert.True(Evaluator.IsDone(Window(title: "settings"), Ctx(now: settings, start: Explorer)));
        Assert.False(Evaluator.IsDone(Window(title: "Control Panel"), Ctx(now: settings, start: Explorer)));
    }

    // ---- elementVisible / elementGone ----

    [Fact]
    public void ElementVisible_follows_the_candidates()
    {
        var done = Visible(Els.T("Text", "Downloads"));

        Assert.False(Evaluator.IsDone(done, Ctx([Els.El("Button", "Other")])));
        Assert.True(Evaluator.IsDone(done, Ctx([Els.El("Text", "Downloads")])));
    }

    [Fact]
    public void ElementVisible_needs_the_exact_role_not_a_look_alike()
    {
        var done = Visible(Els.T("TabItem", "Downloads"));
        var sidebarItem = Els.El("TreeItem", "Start of Quick Access - Downloads (pinned)");

        Assert.False(Evaluator.IsDone(done, Ctx([sidebarItem])));                               // still on This PC
        Assert.True(Evaluator.IsDone(done, Ctx([sidebarItem, Els.El("TabItem", "Downloads")]))); // the folder is open
    }

    [Fact]
    public void ElementGone_treats_a_look_alike_as_gone()
    {
        var gone = new DoneWhen(DoneKind.ElementGone, null, null, Els.T("Button", "OK"), null);

        Assert.True(Evaluator.IsDone(gone, Ctx([Els.El("ListItem", "OK")], seen: true)));
    }

    [Fact]
    public void ElementGone_needs_the_element_to_have_been_seen_first()
    {
        var gone = new DoneWhen(DoneKind.ElementGone, null, null, Els.T("Button", "OK"), null);

        Assert.False(Evaluator.IsDone(gone, Ctx(seen: false)));
        Assert.False(Evaluator.IsDone(gone, Ctx([Els.El("Button", "OK")], seen: true)));
        Assert.True(Evaluator.IsDone(gone, Ctx(seen: true)));
    }

    // ---- toggleState ----

    [Theory]
    [InlineData("on", "on", true)]
    [InlineData("on", "off", false)]
    [InlineData("off", "off", true)]
    [InlineData("off", null, false)]
    public void ToggleState_compares_the_elements_toggle(string wanted, string? actual, bool expected)
    {
        var done = new DoneWhen(DoneKind.ToggleState, null, null, Els.T("Button", "Bluetooth"), wanted);

        Assert.Equal(expected, Evaluator.IsDone(done, Ctx([Els.El("Button", "Bluetooth", toggle: actual)])));
    }

    [Fact]
    public void ToggleState_is_false_when_the_element_is_missing() =>
        Assert.False(Evaluator.IsDone(new DoneWhen(DoneKind.ToggleState, null, null, Els.T("Button", "Bluetooth"), "on"), Ctx()));

    // ---- focusOn ----

    [Fact]
    public void FocusOn_matches_the_focused_element_not_just_any_visible_one()
    {
        var done = new DoneWhen(DoneKind.FocusOn, null, null, Els.T("Edit", "Address bar"), null);
        var visible = new[] { Els.El("Edit", "Address bar") };

        Assert.False(Evaluator.IsDone(done, Ctx(visible, focus: null)));
        Assert.False(Evaluator.IsDone(done, Ctx(visible, focus: new FocusedElement("Edit", "Search", null))));
        Assert.True(Evaluator.IsDone(done, Ctx(visible, focus: new FocusedElement("Edit", "Address bar", null))));
    }

    // ---- userConfirms ----

    [Fact]
    public void UserConfirms_follows_the_flag()
    {
        var done = new DoneWhen(DoneKind.UserConfirms, null, null, null, null);

        Assert.False(Evaluator.IsDone(done, Ctx(confirmed: false)));
        Assert.True(Evaluator.IsDone(done, Ctx(confirmed: true)));
    }
}

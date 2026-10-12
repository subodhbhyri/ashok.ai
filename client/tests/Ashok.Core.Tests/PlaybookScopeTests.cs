using Ashok.Core.Contracts;
using Ashok.Core.Evaluation;
using Ashok.Core.Playbooks;
using Ashok.Core.Session;
using static Ashok.Core.Tests.Steps;

namespace Ashok.Core.Tests;

public class PlaybookScopeTests
{
    private const string Q = "make the text bigger";

    private static UiElement Menu(string name) => new("e1", "MenuItem", name, null, [1, 2, 3, 4], true, null);

    private static Rig NewRig(double score, Playbook book, string process)
    {
        var rig = new Rig([book], new FakeMatcher((Q, book.Id)) { Score = score });
        rig.Windows.ByHandle[11] = new CapturedScreen(new ForegroundState(process, "window", 11), [Menu("Format")]);
        return rig;
    }

    private static Playbook Scaling() => Book("change-display-scaling", Step("s1", Confirm()));

    [Fact]
    public async Task TextBiggerFromNotepad_GoesToThePlannerWithNotepadsScreen()
    {
        var rig = NewRig(0.6, Scaling(), "notepad");

        await rig.Session.AskAsync(Q, 11);

        var request = Assert.Single(rig.Planner.Requests);
        Assert.Equal("notepad", request.Foreground!.Process);
        Assert.Equal("Format", Assert.Single(request.Candidates).Name);
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("SystemSettings")]
    [InlineData("none")]
    public async Task SystemSurfaces_KeepTheNormalThreshold(string process)
    {
        var rig = NewRig(0.6, Scaling(), process);

        await rig.Session.AskAsync(Q, 11);

        Assert.Empty(rig.Planner.Requests);
        Assert.Equal(SessionPhase.Guiding, rig.Session.State.Phase);
    }

    [Fact]
    public async Task TextBiggerFromNotepad_AtTheRealModelsScore_GoesToThePlannerAndNamesTheMatchedPlaybook()
    {
        // The int8 model scores "make the text bigger" against change-display-scaling at 0.86-0.88.
        var rig = NewRig(0.88, Scaling(), "notepad");

        await rig.Session.AskAsync(Q, 11);

        var request = Assert.Single(rig.Planner.Requests);
        Assert.Equal("notepad", request.Foreground!.Process);
        Assert.Equal("change-display-scaling", request.MatchedPlaybookId);
    }

    [Fact]
    public async Task PlannerRequestsFromNoMatchOrOtherReplans_CarryNoHint()
    {
        var rig = new Rig([Scaling()]);
        rig.Windows.Foreground = Rig.Screen("notepad", "Untitled", 11);

        await rig.Session.AskAsync("something unrelated", 11);

        Assert.Null(Assert.Single(rig.Planner.Requests).MatchedPlaybookId);
    }

    [Fact]
    public async Task ARegularApp_StillGetsAPlaybookForAnExactMatch_WithoutAnExtraCapture()
    {
        var rig = NewRig(0.9, Scaling(), "notepad");

        await rig.Session.AskAsync(Q, 11);

        Assert.Empty(rig.Planner.Requests);
        Assert.Equal(SessionPhase.Guiding, rig.Session.State.Phase);
        Assert.Empty(rig.Windows.Calls);
    }

    [Fact]
    public async Task AppScopedPlaybooks_AreNotOverridden()
    {
        var rig = NewRig(0.6, AppBook("notepad-wrap", Step("s1", Confirm())), "notepad");

        await rig.Session.AskAsync(Q, 11);

        Assert.Empty(rig.Planner.Requests);
        Assert.Equal(SessionPhase.Guiding, rig.Session.State.Phase);
    }

    private sealed class RecordingLog : IDebugLog
    {
        public List<(string? Process, string? Playbook, double? Score, string Path)> Lines { get; } = [];

        public void Routed(DateTimeOffset at, string? process, string? playbookId, double? score, string path) =>
            Lines.Add((process, playbookId, score, path));

        public void Verdict(DateTimeOffset at, OffPlanVerdict verdict, string? reason, string process, string stepId, double secondsIntoStep, string action = "replan")
        {
        }

        public void AutomationReset(DateTimeOffset at, string process, int hresult)
        {
        }
    }

    [Fact]
    public async Task EveryQuestionIsLogged_WithProcessMatchAndPath()
    {
        var log = new RecordingLog();
        var book = Scaling();
        var rig = new Rig([book], new FakeMatcher((Q, book.Id)) { Score = 0.88 }, debugLog: log);
        rig.Windows.ByHandle[11] = new CapturedScreen(new ForegroundState("notepad", "t", 11), []);
        rig.Windows.ByHandle[12] = new CapturedScreen(new ForegroundState("explorer", "t", 12), []);

        await rig.Session.AskAsync(Q, 11);
        await rig.Session.AskAsync(Q, 12);
        await rig.Session.AskAsync("unmatched", 11);

        Assert.Equal(
            [("notepad", "change-display-scaling", 0.88, "planner"), ("explorer", "change-display-scaling", 0.88, "playbook"), ("notepad", null, null, "planner")],
            log.Lines);
    }

    private sealed class VerdictLog : IDebugLog
    {
        public List<(OffPlanVerdict Verdict, string? Reason, string Process, string Step, double Secs)> Lines { get; } = [];

        public void Routed(DateTimeOffset at, string? process, string? playbookId, double? score, string path)
        {
        }

        public void AutomationReset(DateTimeOffset at, string process, int hresult)
        {
        }

        public void Verdict(DateTimeOffset at, OffPlanVerdict verdict, string? reason, string process, string stepId, double secondsIntoStep, string action = "replan") =>
            Lines.Add((verdict, reason, process, stepId, secondsIntoStep));
    }

    [Fact]
    public async Task ANonOnPlanVerdict_IsLoggedWithReasonProcessStepAndSeconds()
    {
        var log = new VerdictLog();
        var book = Book("x", Step("s1", Visible("Button", "Never appears")));
        var rig = new Rig([book], new FakeMatcher((Q, "x")), detector: new HeuristicOffPlanDetector(), debugLog: log);

        await rig.Session.AskAsync(Q, 0);
        await rig.Tick(Rig.Screen("SystemSettings", "Settings", 1));
        Assert.Empty(log.Lines);

        rig.Time.Advance(TimeSpan.FromSeconds(26)); // a step with nothing to point at: stuck after 25 s
        await rig.Tick(Rig.Screen("SystemSettings", "Settings", 1));

        var line = Assert.Single(log.Lines);
        Assert.Equal((OffPlanVerdict.Stuck, "stuck", "SystemSettings", "s1"), (line.Verdict, line.Reason, line.Process, line.Step));
        Assert.InRange(line.Secs, 25.9, 26.1);
    }

    [Fact]
    public void RegularApp_Classification()
    {
        Assert.True(SystemSurfaces.IsRegularApp("notepad"));
        Assert.True(SystemSurfaces.IsRegularApp("chrome.exe"));
        Assert.False(SystemSurfaces.IsRegularApp("Explorer.exe"));
        Assert.False(SystemSurfaces.IsRegularApp("SystemSettings"));
        Assert.False(SystemSurfaces.IsRegularApp(""));
    }

    [Fact]
    public void Loader_ReadsScope_AndRejectsUnknownOrMissingOnes()
    {
        const string yaml = "id: x\ntitle: X\nSCOPEos: [win11]\nexamples: [a]\nsteps:\n  - id: s1\n    say: Look.\n    action: observe\n    doneWhen: { kind: userConfirms }\n";
        string With(string scope) => yaml.Replace("SCOPE", scope);

        Assert.Equal(PlaybookScope.App, PlaybookLoader.Parse(With("scope: app\n"), "x").Scope);
        Assert.Equal(PlaybookScope.System, PlaybookLoader.Parse(With("scope: system\n"), "x").Scope);
        Assert.Throws<PlaybookException>(() => PlaybookLoader.Parse(With("scope: everywhere\n"), "x"));
        Assert.Throws<PlaybookException>(() => PlaybookLoader.Parse(With(""), "x"));
    }
}

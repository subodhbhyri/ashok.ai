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
    public async Task ARegularApp_StillGetsAPlaybookForAVeryCloseMatch_WithoutAnExtraCapture()
    {
        var rig = NewRig(0.75, Scaling(), "notepad");

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

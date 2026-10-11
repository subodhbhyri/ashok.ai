using Ashok.Core.Contracts;
using Ashok.Core.Session;

namespace Ashok.Core.Tests;

public class AskCaptureTests
{
    private static UiElement Button(string name) => new("e1", "Button", name, null, [1, 2, 3, 4], true, null);

    private static Rig NewRig() => new([]);

    [Fact]
    public async Task FirstPlannerRequest_CarriesTheSnapshotOfThePreviousWindow()
    {
        var rig = NewRig();
        rig.Windows.ByHandle[42] = new CapturedScreen(new Evaluation.ForegroundState("explorer", "This PC", 42), [Button("New")], Redacted: 3);
        rig.Windows.Foreground = Rig.Screen("Ashok.App", "Ask");

        await rig.Session.AskAsync("how do I make a new folder", 42);

        var request = Assert.Single(rig.Planner.Requests);
        Assert.Equal(PlanReason.New, request.Reason);
        Assert.Equal("explorer", request.Foreground!.Process);
        Assert.Equal("New", Assert.Single(request.Candidates).Name);
        Assert.Equal(3, request.Redacted);
    }

    [Fact]
    public async Task PreviousWindowGone_FallsBackToTheForegroundWindow()
    {
        var rig = NewRig();
        rig.Windows.Foreground = new CapturedScreen(new Evaluation.ForegroundState("notepad", "Untitled", 7), [Button("File")]);

        await rig.Session.AskAsync("q", 999);

        Assert.Equal(["window 999", "foreground"], rig.Windows.Calls);
        Assert.Equal("notepad", rig.Planner.Requests.Single().Foreground!.Process);
    }

    [Fact]
    public async Task NeverSendsACaptureOfAshokItself()
    {
        var rig = NewRig();
        rig.Windows.ByHandle[5] = new CapturedScreen(new Evaluation.ForegroundState(Rig.Own, "Ask", 5), [Button("Send")]);
        rig.Windows.Foreground = new CapturedScreen(new Evaluation.ForegroundState(Rig.Own, "Ask", 5), [Button("Send")]);

        await rig.Session.AskAsync("q", 5);

        var request = Assert.Single(rig.Planner.Requests);
        Assert.Null(request.Foreground);
        Assert.Empty(request.Candidates);
    }

    [Fact]
    public async Task OwnPreviousWindow_FallsBackToAForegroundThatIsNotAshok()
    {
        var rig = NewRig();
        rig.Windows.ByHandle[5] = new CapturedScreen(new Evaluation.ForegroundState(Rig.Own, "Ask", 5), []);
        rig.Windows.Foreground = new CapturedScreen(new Evaluation.ForegroundState("explorer", "This PC", 9), [Button("New")]);

        await rig.Session.AskAsync("q", 5);

        Assert.Equal("explorer", rig.Planner.Requests.Single().Foreground!.Process);
    }
}

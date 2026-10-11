using Ashok.Core.Contracts;
using Ashok.Core.Evaluation;
using Ashok.Core.Overlay;
using Ashok.Core.Playbooks;
using Ashok.Core.Resolution;
using Ashok.Core.Session;
using Ashok.Core.Speech;

namespace Ashok.Core.Tests;

internal sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeOverlay : IOverlay
{
    public List<string> Calls { get; } = [];

    public List<ScreenRect> Shown { get; } = [];

    public bool Pulse { get; set; } = true;

    public void Show(ScreenRect target)
    {
        Shown.Add(target);
        Calls.Add($"show {target.X},{target.Y}");
    }

    public void Hide() => Calls.Add("hide");
}

internal sealed class FakeSpeaker : ISpeaker
{
    public List<string> Spoken { get; } = [];

    public int Stops { get; private set; }

    public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        Spoken.Add(text);
        return Task.CompletedTask;
    }

    public void Stop() => Stops++;
}

internal sealed class FakeView : ISessionView
{
    public List<StepView> Steps { get; } = [];

    public List<string> Messages { get; } = [];

    public int Clears { get; private set; }

    public void ShowStep(StepView step) => Steps.Add(step);

    public void ShowMessage(string text) => Messages.Add(text);

    public void Clear() => Clears++;
}

internal sealed class FakeMatcher(params (string Question, string PlaybookId)[] known) : IIntentMatcher
{
    public IntentMatch? Match(string question) =>
        known.FirstOrDefault(k => k.Question == question) is { PlaybookId: { } id } ? new IntentMatch(id, 0.9) : null;
}

/// <summary>Windows by handle plus a current foreground; a handle not in the table is a window that is gone.</summary>
internal sealed class FakeWindows : ISnapshotSource
{
    public Dictionary<long, CapturedScreen> ByHandle { get; } = [];

    public CapturedScreen Foreground { get; set; } = new(new ForegroundState("none", "", 0), []);

    public List<string> Calls { get; } = [];

    public CapturedScreen Capture()
    {
        Calls.Add("foreground");
        return Foreground;
    }

    public CapturedScreen? CaptureWindow(long hwnd)
    {
        Calls.Add($"window {hwnd}");
        return ByHandle.GetValueOrDefault(hwnd);
    }
}

internal sealed class FakePlanner : IPlanner
{
    public List<PlanRequest> Requests { get; } = [];

    public Func<PlanRequest, CancellationToken, Task<PlannerResult>> Handler { get; set; } =
        (_, _) => Task.FromResult(PlannerResult.Unavailable("test"));

    public Task<PlannerResult> PlanAsync(PlanRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Handler(request, cancellationToken);
    }
}

/// <summary>Fails the test if anything asks for a plan: a playbook session must never need the model.</summary>
internal sealed class TripwirePlanner : IPlanner
{
    public int Calls { get; private set; }

    public Task<PlannerResult> PlanAsync(PlanRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("The planner (the only network path) was called during an offline session.");
    }
}

internal static class Steps
{
    public static PlanStep Step(string id, DoneWhen done, Target? target = null, string? say = null, ActionKind action = ActionKind.Click) =>
        new(id, say ?? $"Do {id}.", action, target, null, done);

    public static DoneWhen Visible(string role, string name) =>
        new(DoneKind.ElementVisible, null, null, new Target(null, role, name, null), null);

    public static DoneWhen Gone(string role, string name) =>
        new(DoneKind.ElementGone, null, null, new Target(null, role, name, null), null);

    public static DoneWhen Opened(string process) => new(DoneKind.WindowOpened, process, null, null, null);

    public static DoneWhen Confirm() => new(DoneKind.UserConfirms, null, null, null, null);

    public static Playbook Book(string id, params PlanStep[] steps) => new(id, id, ["win11"], ["example"], steps);
}

/// <summary>A session wired to fakes, with a clock the test controls.</summary>
internal sealed class Rig
{
    public const string Own = "Ashok.App";

    public FakeTime Time { get; } = new();
    public FakeOverlay Overlay { get; } = new();
    public FakeSpeaker Speaker { get; } = new();
    public FakeView View { get; } = new();
    public FakePlanner Planner { get; } = new();
    public GuidanceSession Session { get; }
    public List<SessionState> States { get; } = [];

    public FakeWindows Windows { get; } = new();

    public Rig(IEnumerable<Playbook> playbooks, FakeMatcher? matcher = null, IPlanner? planner = null, IOffPlanDetector? detector = null)
    {
        var resolver = new TargetResolver();
        Session = new GuidanceSession(
            matcher ?? new FakeMatcher(),
            new PlaybookStore(playbooks),
            planner ?? Planner,
            detector ?? new NaiveOffPlanDetector(),
            resolver,
            new DoneEvaluator(resolver),
            Overlay, Speaker, View, new InlineDispatcher(), Time, Own, Windows);
        Session.StateChanged += States.Add;
    }

    public static CapturedScreen Screen(string process = "SystemSettings", string title = "Settings", long handle = 1, params UiElement[] elements) =>
        new(new ForegroundState(process, title, handle), elements);

    public Task Tick(CapturedScreen screen, FocusedElement? focus = null) => Session.TickAsync(screen, focus);
}

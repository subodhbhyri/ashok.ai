using Ashok.Core.Contracts;
using Ashok.Core.Evaluation;

namespace Ashok.Core.Session;

/// <summary>What one capture of the foreground window produced: who is in front and the unranked candidates.</summary>
public sealed record CapturedScreen(ForegroundState Foreground, IReadOnlyList<UiElement> Candidates);

/// <summary>Captures the screen for the session. The Windows implementation wraps UI Automation.</summary>
public interface ISnapshotSource
{
    /// <summary>Called off the UI thread and never concurrently with itself.</summary>
    CapturedScreen Capture();
}

public interface IFocusProbe
{
    FocusedElement? Get();
}

/// <summary>Tells the runner "something may have changed, look again". It does no UI Automation work itself.</summary>
public interface IUiWatcher
{
    /// <summary>Starts signalling; <paramref name="onChange"/> may be called from any thread. Dispose to stop.</summary>
    IDisposable Start(Action onChange);
}

public sealed record IntentMatch(string PlaybookId, double Score);

public interface IIntentMatcher
{
    /// <summary>The best playbook for the question, or null when nothing is close enough.</summary>
    IntentMatch? Match(string question);
}

public enum PlanReason { New, OffPlan, Stuck }

public sealed record StepRecord(string StepId, string Say, bool Completed);

public sealed record PlanRequest(
    string Question,
    PlanReason Reason,
    IReadOnlyList<StepRecord> History,
    ForegroundState? Foreground,
    IReadOnlyList<UiElement> Candidates);

/// <param name="Plan">A usable plan, or null.</param>
/// <param name="Failure">Why there is no plan, in words fit for logs (never shown to the user verbatim).</param>
public sealed record PlannerResult(Plan? Plan, string? Failure)
{
    public static PlannerResult Unavailable(string why) => new(null, why);
}

/// <summary>Asks a model for a plan. Implemented in Phase 4; until then <see cref="NullPlanner"/>.</summary>
public interface IPlanner
{
    Task<PlannerResult> PlanAsync(PlanRequest request, CancellationToken cancellationToken);
}

public sealed class NullPlanner : IPlanner
{
    public Task<PlannerResult> PlanAsync(PlanRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(PlannerResult.Unavailable("no planner is configured"));
}

public sealed record StepView(string Say, int Index, int Total, bool NeedsConfirm);

/// <summary>What the user sees. Always called on the UI thread, through <see cref="IUiDispatcher"/>.</summary>
public interface ISessionView
{
    void ShowStep(StepView step);

    void ShowMessage(string text);

    void Clear();
}

/// <summary>Runs an action on the UI thread without waiting for it.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public enum OffPlanVerdict { OnPlan, OffPlan, Stuck }

public sealed record OffPlanContext(
    ForegroundState Foreground,
    IReadOnlySet<string> KnownProcesses,
    PlanStep Step,
    DateTimeOffset StepStartedAt,
    DateTimeOffset LastProgressAt,
    DateTimeOffset Now);

// OPUS: off-plan and stuck heuristics (plan "Tricky parts", phases 3-4). Too eager feels naggy and burns
// planner calls; too lax leaves the user lost. Decide what "unexpected foreground window" means (shell
// surfaces such as Start and Search, dialogs of the same program, UAC), how long it must persist, what
// counts as progress, and the stuck timeout. The session calls Reset when a guide or step begins and Check
// on every tick; it already ignores Ashok's own windows. NaiveOffPlanDetector is the placeholder.
public interface IOffPlanDetector
{
    void Reset(DateTimeOffset now);

    OffPlanVerdict Check(OffPlanContext context);
}

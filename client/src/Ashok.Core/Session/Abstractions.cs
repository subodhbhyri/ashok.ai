using Ashok.Core.Contracts;
using Ashok.Core.Evaluation;

namespace Ashok.Core.Session;

/// <summary>What one capture of the foreground window produced: who is in front and the unranked candidates.</summary>
/// <param name="Redacted">How many elements redaction dropped (passwords and the like); sent to the planner as a count only.</param>
public sealed record CapturedScreen(ForegroundState Foreground, IReadOnlyList<UiElement> Candidates, int Redacted = 0);

/// <summary>Captures the screen for the session. The Windows implementation wraps UI Automation.</summary>
public interface ISnapshotSource
{
    /// <summary>Called off the UI thread and never concurrently with itself.</summary>
    CapturedScreen Capture();

    /// <summary>
    /// Captures a specific window, for the window the user was in when they asked.
    /// Null when that window no longer exists. Same threading rules as <see cref="Capture"/>.
    /// </summary>
    CapturedScreen? CaptureWindow(long hwnd);
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

/// <summary>A local, content-free record of how each question was routed. Never gets the question text or screen contents.</summary>
public interface IDebugLog
{
    /// <param name="process">Foreground process of the captured window, or null when no capture was needed.</param>
    void Routed(DateTimeOffset at, string? process, string? playbookId, double? score, string path);

    /// <summary>A replan verdict during guidance. Only ids, names and numbers: never screen contents.</summary>
    void Verdict(DateTimeOffset at, OffPlanVerdict verdict, string? reason, string process, string stepId, double secondsIntoStep);
}

public sealed class NullDebugLog : IDebugLog
{
    public void Routed(DateTimeOffset at, string? process, string? playbookId, double? score, string path)
    {
    }

    public void Verdict(DateTimeOffset at, OffPlanVerdict verdict, string? reason, string process, string stepId, double secondsIntoStep)
    {
    }
}

public enum PlanReason { New, OffPlan, Stuck }

public sealed record StepRecord(string StepId, string Say, bool Completed);

public sealed record PlanRequest(
    string Question,
    PlanReason Reason,
    IReadOnlyList<StepRecord> History,
    ForegroundState? Foreground,
    IReadOnlyList<UiElement> Candidates,
    int Redacted = 0,
    string? MatchedPlaybookId = null);

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
    DateTimeOffset Now,
    bool? TargetOnScreen = null);

/// <summary>
/// The session calls Reset when a guide or step begins and Check on every tick; it already ignores Ashok's own windows.
/// <see cref="OffPlanContext.TargetOnScreen"/> is null when the step has no target, otherwise whether the resolver found it.
/// </summary>
public interface IOffPlanDetector
{
    void Reset(DateTimeOffset now);

    OffPlanVerdict Check(OffPlanContext context);

    /// <summary>Why the last OffPlan or Stuck verdict was returned, for the debug log. Detectors that do not track it return null.</summary>
    string? LastReason => null;
}

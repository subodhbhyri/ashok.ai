using Ashok.Core.Contracts;
using Ashok.Core.Evaluation;
using Ashok.Core.Overlay;
using Ashok.Core.Playbooks;
using Ashok.Core.Resolution;
using Ashok.Core.Speech;

namespace Ashok.Core.Session;

public enum SessionPhase { Idle, Matching, Guiding, Replanning, Done, GaveUp }

public sealed record SessionState(SessionPhase Phase, string? GuideId = null, int StepIndex = -1);

/// <summary>
/// The session state machine: Idle, Matching, Guiding(step), Replanning, then Done or GaveUp.
/// It owns no threads and no timers. The caller feeds it questions and screen captures, and every
/// call is safe to make from any thread. Results reach the user through the view, overlay and speaker,
/// always via <see cref="IUiDispatcher"/>. It never clicks or types: it only points and speaks.
/// </summary>
public sealed class GuidanceSession(
    IIntentMatcher matcher,
    IPlaybookStore playbooks,
    IPlanner planner,
    IOffPlanDetector offPlan,
    TargetResolver resolver,
    DoneEvaluator evaluator,
    IOverlay overlay,
    ISpeaker speaker,
    ISessionView view,
    IUiDispatcher dispatcher,
    TimeProvider time,
    string ownProcess,
    ISnapshotSource? snapshots = null)
{
    public const string NotSureMessage =
        "I'm not sure how to help with that yet. It might be best to ask someone you trust.";

    public const string LostMessage =
        "I've lost track of where we are. It might be best to ask someone you trust.";

    /// <summary>
    /// When the user is inside a regular app, a system playbook is used only for a match at least this close;
    /// a weaker one goes to the planner, which can see the app. Explorer, Settings and the desktop keep the matcher's threshold.
    /// </summary>
    public const double AppWindowMinScore = 0.75;

    public const string DoneMessage = "All done. Well done!";

    private sealed class StepRun(DateTimeOffset startedAt)
    {
        public ForegroundState? Start;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public bool Seen;
        public bool Confirmed;
    }

    private readonly object _gate = new();
    private readonly HashSet<string> _knownProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StepRecord> _history = [];

    private int _epoch;
    private CancellationTokenSource? _plannerCts;
    private string _question = "";
    private IReadOnlyList<PlanStep>? _steps;
    private int _stepIndex;
    private StepRun _run = new(default);
    private DateTimeOffset _lastProgress;
    private ScreenRect? _ringShown;
    private bool _awaitingFirstScreen;

    public SessionState State { get; private set; } = new(SessionPhase.Idle);

    /// <summary>Raised on every state change, while the session's lock is held; handlers must be quick.</summary>
    public event Action<SessionState>? StateChanged;

    // ---- inputs ---------------------------------------------------------------------------------

    /// <summary>The user asked a question. Starts a playbook if one fits, otherwise asks the planner.</summary>
    public async Task AskAsync(string question, long previousWindow)
    {
        int epoch;
        lock (_gate)
        {
            epoch = ResetLocked();
            _question = question;
            Set(new SessionState(SessionPhase.Matching));
        }

        var match = matcher.Match(question);
        var playbook = match is null ? null : playbooks.Find(match.PlaybookId);

        // The planner needs to see the window the user asked about, which is not Ashok's own panel.
        // AskAsync already runs off the UI thread (SessionRunner). The capture is skipped when a playbook
        // clearly wins: otherwise it also tells us whether the user is inside one of their own apps.
        CapturedScreen? screen = null;
        if (playbook is null || NeedsAppCheck(playbook, match!))
        {
            screen = CapturePreviousWindow(previousWindow);
            if (playbook is not null && screen is not null && SystemSurfaces.IsRegularApp(screen.Foreground.Process))
                playbook = null; // a system playbook must not override the app the user is in
        }

        lock (_gate)
        {
            if (epoch != _epoch) return;
            if (playbook is not null)
            {
                StartGuideLocked(playbook.Id, playbook.Steps);
                return;
            }
        }
        await ReplanAsync(PlanReason.New, epoch, screen);
    }

    private static bool NeedsAppCheck(Playbook playbook, IntentMatch match) =>
        playbook.Scope == PlaybookScope.System && match.Score < AppWindowMinScore;

    private CapturedScreen? CapturePreviousWindow(long previousWindow)
    {
        if (snapshots is null) return null;
        try
        {
            var screen = previousWindow != 0 ? snapshots.CaptureWindow(previousWindow) : null;
            if (screen is null || IsOwn(screen)) screen = snapshots.Capture(); // that window is gone: use whatever is in front now
            return IsOwn(screen) ? null : screen;
        }
        catch (Exception)
        {
            return null; // a window that closes mid-capture must not stop the question from being answered
        }
    }

    private bool IsOwn(CapturedScreen screen) =>
        string.Equals(screen.Foreground.Process, ownProcess, StringComparison.OrdinalIgnoreCase);

    /// <summary>A fresh capture of the foreground window. Advances, re-points the ring, or detects trouble.</summary>
    public async Task TickAsync(CapturedScreen screen, FocusedElement? focus)
    {
        int epoch;
        var verdict = OffPlanVerdict.OnPlan;

        lock (_gate)
        {
            if (State.Phase != SessionPhase.Guiding || _steps is null) return;
            // Ashok's own windows are never the user's task; their elements must not satisfy a step.
            if (string.Equals(screen.Foreground.Process, ownProcess, StringComparison.OrdinalIgnoreCase)) return;

            epoch = _epoch;
            var now = time.GetUtcNow();
            var announce = false;

            // The window the user starts in is, by definition, expected.
            if (_awaitingFirstScreen)
            {
                _knownProcesses.Add(screen.Foreground.Process);
                _awaitingFirstScreen = false;
            }

            // A step can already be satisfied (the user got ahead of us), so keep advancing while it is.
            for (var guard = 0; guard <= _steps.Count; guard++)
            {
                var step = _steps[_stepIndex];
                _run.Start ??= screen.Foreground;
                if (!_run.Seen && step.DoneWhen.Kind == DoneKind.ElementGone)
                    _run.Seen = evaluator.TargetPresent(step.DoneWhen, screen.Candidates);

                var context = new EvaluationContext(screen.Candidates, screen.Foreground, _run.Start, focus, _run.Confirmed, _run.Seen);
                if (!evaluator.IsDone(step.DoneWhen, context)) break;

                _history.Add(new StepRecord(step.Id, step.Say, true));
                _knownProcesses.Add(screen.Foreground.Process);
                _lastProgress = now;

                if (_stepIndex + 1 >= _steps.Count)
                {
                    FinishLocked(SessionPhase.Done, DoneMessage);
                    return;
                }

                _stepIndex++;
                _run = new StepRun(now);
                offPlan.Reset(now);
                announce = true;
                Set(State with { StepIndex = _stepIndex });
            }

            var current = _steps[_stepIndex];
            if (announce) AnnounceLocked(current);
            UpdateRingLocked(current, screen);

            verdict = offPlan.Check(new OffPlanContext(
                screen.Foreground, _knownProcesses, current, _run.StartedAt, _lastProgress, now));
            if (verdict != OffPlanVerdict.OnPlan)
                _knownProcesses.Add(screen.Foreground.Process); // do not trigger again for the same window mid-replan
        }

        if (verdict != OffPlanVerdict.OnPlan)
            await ReplanAsync(verdict == OffPlanVerdict.OffPlan ? PlanReason.OffPlan : PlanReason.Stuck, epoch, screen);
    }

    /// <summary>The user says they did the current step. Only meaningful for a <c>userConfirms</c> step.</summary>
    public bool Confirm()
    {
        lock (_gate)
        {
            if (State.Phase != SessionPhase.Guiding || _steps is null) return false;
            if (_steps[_stepIndex].DoneWhen.Kind != DoneKind.UserConfirms) return false;
            _run.Confirmed = true;
            return true;
        }
    }

    /// <summary>The user stopped. Back to Idle from any state.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            ResetLocked();
            Set(new SessionState(SessionPhase.Idle));
        }
    }

    // ---- internals ------------------------------------------------------------------------------

    private async Task ReplanAsync(PlanReason reason, int epoch, CapturedScreen? screen)
    {
        CancellationToken token;
        PlanRequest request;
        lock (_gate)
        {
            if (epoch != _epoch) return;
            _plannerCts = new CancellationTokenSource();
            token = _plannerCts.Token;
            HideRingLocked();
            Set(new SessionState(SessionPhase.Replanning, State.GuideId, State.StepIndex));
            request = new PlanRequest(_question, reason, _history.ToList(), screen?.Foreground, screen?.Candidates ?? [], screen?.Redacted ?? 0);
        }

        PlannerResult result;
        try
        {
            result = await planner.PlanAsync(request, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            result = PlannerResult.Unavailable(e.Message);
        }

        lock (_gate)
        {
            if (epoch != _epoch) return;

            if (result.Plan is { Refusal: null, Steps.Count: > 0 } plan)
            {
                StartGuideLocked(plan.Intent, plan.Steps);
                return;
            }
            if (result.Plan?.Refusal is { } refusal)
            {
                FinishLocked(SessionPhase.GaveUp, refusal);
                return;
            }
            FinishLocked(SessionPhase.GaveUp, reason == PlanReason.New ? NotSureMessage : LostMessage);
        }
    }

    private void StartGuideLocked(string guideId, IReadOnlyList<PlanStep> steps)
    {
        var now = time.GetUtcNow();
        _steps = steps;
        _stepIndex = 0;
        _run = new StepRun(now);
        _history.Clear();
        _lastProgress = now;
        _knownProcesses.Clear();
        foreach (var process in steps.Select(s => s.DoneWhen.Process).Where(p => p is not null))
            _knownProcesses.Add(process!);
        _awaitingFirstScreen = true;
        offPlan.Reset(now);

        Set(new SessionState(SessionPhase.Guiding, guideId, 0));
        AnnounceLocked(steps[0]);
    }

    private void AnnounceLocked(PlanStep step)
    {
        var steps = _steps!;
        var number = _stepIndex;
        var needsConfirm = step.DoneWhen.Kind == DoneKind.UserConfirms;
        dispatcher.Post(() =>
        {
            view.ShowStep(new StepView(step.Say, number, steps.Count, needsConfirm));
            _ = speaker.SpeakAsync(step.Say);
        });
    }

    private void UpdateRingLocked(PlanStep step, CapturedScreen screen)
    {
        var found = step.Target is { } target ? resolver.Best(target, screen.Candidates) : null;
        if (found is null)
        {
            HideRingLocked();
            return;
        }

        var rect = ScreenRect.FromBounds(found.Element.Bounds);
        if (_ringShown == rect) return;
        _ringShown = rect;
        dispatcher.Post(() => overlay.Show(rect));
    }

    private void HideRingLocked()
    {
        if (_ringShown is null) return;
        _ringShown = null;
        dispatcher.Post(overlay.Hide);
    }

    private void FinishLocked(SessionPhase phase, string message)
    {
        HideRingLocked();
        _steps = null;
        Set(new SessionState(phase, State.GuideId, State.StepIndex));
        dispatcher.Post(() =>
        {
            view.ShowMessage(message);
            _ = speaker.SpeakAsync(message);
        });
    }

    // Invalidates in-flight work (planner results, a half-finished Ask) and silences everything.
    private int ResetLocked()
    {
        _epoch++;
        _plannerCts?.Cancel();
        _plannerCts = null;
        _steps = null;
        HideRingLocked();
        dispatcher.Post(() =>
        {
            speaker.Stop();
            view.Clear();
        });
        return _epoch;
    }

    private void Set(SessionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}

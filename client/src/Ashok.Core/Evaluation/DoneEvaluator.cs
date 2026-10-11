using Ashok.Core.Contracts;
using Ashok.Core.Resolution;

namespace Ashok.Core.Evaluation;

/// <summary>The window in front, with its handle so a second window of the same program is distinguishable.</summary>
public sealed record ForegroundState(string Process, string Title, long Handle);

/// <summary>The element that has keyboard focus. Not part of the snapshot contract, so it travels separately.</summary>
public sealed record FocusedElement(string Role, string Name, string? AutomationId);

/// <param name="Candidates">Unranked candidates, never the ranker's output.</param>
/// <param name="AtStepStart">Foreground when the step began; null when unknown.</param>
/// <param name="ElementSeenSinceStart">Whether an <c>elementGone</c> target has been seen yet during this step.</param>
public sealed record EvaluationContext(
    IReadOnlyList<UiElement> Candidates,
    ForegroundState Foreground,
    ForegroundState? AtStepStart,
    FocusedElement? Focused,
    bool UserConfirmed,
    bool ElementSeenSinceStart);

/// <summary>Decides whether a step's <c>doneWhen</c> condition holds. Pure: no UI Automation, no clock.</summary>
public sealed class DoneEvaluator(TargetResolver resolver)
{
    public bool IsDone(DoneWhen condition, EvaluationContext context) => condition.Kind switch
    {
        DoneKind.WindowOpened => WindowOpened(condition, context),
        DoneKind.ElementVisible => condition.Target is { } t && resolver.BestExactRole(t, context.Candidates) is not null,
        DoneKind.ElementGone => condition.Target is { } t && context.ElementSeenSinceStart &&
                                resolver.BestExactRole(t, context.Candidates) is null,
        DoneKind.ToggleState => ToggleState(condition, context),
        DoneKind.FocusOn => FocusOn(condition, context),
        DoneKind.UserConfirms => context.UserConfirmed,
        _ => false,
    };

    /// <summary>True once the condition's target is on screen; used to arm <c>elementGone</c>.</summary>
    public bool TargetPresent(DoneWhen condition, IReadOnlyList<UiElement> candidates) =>
        condition.Target is { } t && resolver.BestExactRole(t, candidates) is not null;

    private static bool Matches(DoneWhen condition, ForegroundState window) =>
        (condition.Process is null || string.Equals(Strip(condition.Process), Strip(window.Process), StringComparison.OrdinalIgnoreCase)) &&
        (condition.Title is null || window.Title.Contains(condition.Title, StringComparison.OrdinalIgnoreCase));

    // "opened": it matches now, and either it did not match when the step began or it is a different window.
    private static bool WindowOpened(DoneWhen condition, EvaluationContext context)
    {
        if (!Matches(condition, context.Foreground)) return false;
        return context.AtStepStart is null ||
               !Matches(condition, context.AtStepStart) ||
               context.AtStepStart.Handle != context.Foreground.Handle;
    }

    private bool ToggleState(DoneWhen condition, EvaluationContext context)
    {
        if (condition.Target is not { } target || condition.State is not { } state) return false;
        var found = resolver.BestExactRole(target, context.Candidates);
        return found is not null && string.Equals(found.Element.Toggle, state, StringComparison.OrdinalIgnoreCase);
    }

    private bool FocusOn(DoneWhen condition, EvaluationContext context)
    {
        if (condition.Target is not { } target || context.Focused is not { } focused) return false;
        var asElement = new UiElement("e0", focused.Role, focused.Name, focused.AutomationId, [0, 0, 1, 1], true, null);
        return resolver.BestExactRole(target, [asElement]) is not null;
    }

    private static string Strip(string process) =>
        process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process[..^4] : process;
}

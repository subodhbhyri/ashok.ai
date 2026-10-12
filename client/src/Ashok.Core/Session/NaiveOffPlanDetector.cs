namespace Ashok.Core.Session;

// A baseline for tests: the plan's one-line description, an unexpected foreground window or 45 s with no progress.
// The app uses HeuristicOffPlanDetector.
public sealed class NaiveOffPlanDetector : IOffPlanDetector
{
    public static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(45);

    /// <summary>How long an unexpected window must stay in front before it counts, so a flicker is not off-plan.</summary>
    public static readonly TimeSpan UnexpectedFor = TimeSpan.FromMilliseconds(1500);

    // Windows shell surfaces that appear during normal guidance (the Start menu, Search, UWP frames).
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "ApplicationFrameHost", "TextInputHost",
    };

    private DateTimeOffset? _unexpectedSince;

    public void Reset(DateTimeOffset now) => _unexpectedSince = null;

    public OffPlanVerdict Check(OffPlanContext context)
    {
        var known = context.KnownProcesses.Contains(context.Foreground.Process) ||
                    ShellProcesses.Contains(context.Foreground.Process);
        if (known) _unexpectedSince = null;
        else _unexpectedSince ??= context.Now;

        if (_unexpectedSince is { } since && context.Now - since >= UnexpectedFor) return OffPlanVerdict.OffPlan;
        if (context.Now - context.LastProgressAt >= StuckAfter) return OffPlanVerdict.Stuck;
        return OffPlanVerdict.OnPlan;
    }
}

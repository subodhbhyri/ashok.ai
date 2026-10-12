using Ashok.Core.Contracts;

namespace Ashok.Core.Session;

/// <summary>
/// Decides when the person has wandered off the plan or is stuck. It trades two failures against each other:
/// replanning too eagerly feels naggy and spends planner calls; too late leaves an anxious person lost.
///
/// <para><b>Off plan</b>, in two ways:</para>
/// <list type="bullet">
/// <item>A different app stays in front for <see cref="UnexpectedAppFor"/>. Windows' own shell surfaces
/// (Start, Search, the taskbar, quick settings, UAC prompts, notifications) never count; neither do the
/// processes the plan expects or that the person already used during this guide.</item>
/// <item>Within an expected app, the thing the step points at was on screen during this step and has now
/// been gone for <see cref="TargetLostFor"/>: they went to another page. Long enough to let a page load or
/// survive a brief scroll.</item>
/// </list>
///
/// <para><b>Stuck</b>: no activity for a time that depends on how much help the step gives. A ring on screen
/// is the most help (40 s); a step with nothing to point at, such as a key press or a target on a page not
/// yet open, gets 25 s; looking gets 60 s; typing something and pressing Done gets 120 s. Any change of window
/// or title counts as activity and restarts the clock, because the person is clearly trying.</para>
///
/// <para><b>Budget</b>: at most one replan verdict every <see cref="MinGapBetweenReplans"/> and
/// <see cref="MaxReplansPerWindow"/> per <see cref="ReplanWindow"/>, across questions. Past that the guide
/// simply carries on, which costs nothing and keeps a confused person from being re-instructed in a loop.</para>
///
/// One instance is shared by every session. <see cref="Reset"/> runs when a guide or a step begins and clears
/// per-step state; the budget survives it.
/// </summary>
public sealed class HeuristicOffPlanDetector : IOffPlanDetector
{
    public static readonly TimeSpan UnexpectedAppFor = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan TargetLostFor = TimeSpan.FromSeconds(8);

    public static readonly TimeSpan StuckWithRing = TimeSpan.FromSeconds(40);
    public static readonly TimeSpan StuckWithoutRing = TimeSpan.FromSeconds(25);
    public static readonly TimeSpan StuckWhileObserving = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan StuckWhileTyping = TimeSpan.FromSeconds(120);

    public static readonly TimeSpan MinGapBetweenReplans = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan ReplanWindow = TimeSpan.FromMinutes(10);
    public const int MaxReplansPerWindow = 4;

    /// <summary>Windows' own surfaces, which appear during normal guidance and never mean "somewhere else".</summary>
    public static readonly IReadOnlySet<string> ShellProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "none",                          // nothing in front, for example the bare desktop
        "explorer",                      // desktop, taskbar, File Explorer
        "SearchHost", "SearchApp",       // Search
        "StartMenuExperienceHost",       // Start
        "ShellExperienceHost", "ShellHost", // notifications, quick settings, calendar flyout
        "ApplicationFrameHost",          // the frame around Store apps
        "TextInputHost",                 // touch keyboard, emoji panel
        "consent",                       // UAC prompt
        "LockApp", "LogonUI",            // lock screen, sign-in
        "PickerHost", "OpenWith",        // file pickers, "open with" chooser
        "SystemSettingsAdminFlows",      // Settings' elevated helpers
    };

    private readonly Queue<DateTimeOffset> _replans = new();

    /// <summary>"other-app", "target-lost" or "stuck": why the last OffPlan or Stuck verdict was returned.</summary>
    public string? LastReason { get; private set; }

    private long _handle;
    private string? _title;
    private DateTimeOffset _activityAt;
    private DateTimeOffset? _unexpectedSince;
    private long _unexpectedHandle;
    private bool _targetSeen;
    private DateTimeOffset? _targetLostSince;

    public void Reset(DateTimeOffset now)
    {
        _activityAt = now;
        _unexpectedSince = null;
        _unexpectedHandle = 0;
        _targetSeen = false;
        _targetLostSince = null;
    }

    public OffPlanVerdict Check(OffPlanContext context)
    {
        var now = context.Now;
        var foreground = context.Foreground;

        // Any change of window or title is the person doing something.
        if (foreground.Handle != _handle || !string.Equals(foreground.Title, _title, StringComparison.Ordinal))
        {
            _handle = foreground.Handle;
            _title = foreground.Title;
            _activityAt = now;
        }

        // 1. Somewhere else entirely.
        var expected = context.KnownProcesses.Contains(foreground.Process) || ShellProcesses.Contains(foreground.Process);
        if (expected)
        {
            _unexpectedSince = null;
        }
        else if (_unexpectedSince is null || _unexpectedHandle != foreground.Handle)
        {
            _unexpectedSince = now;
            _unexpectedHandle = foreground.Handle;
        }

        if (_unexpectedSince is { } away && now - away >= UnexpectedAppFor)
            return Spend(OffPlanVerdict.OffPlan, "other-app", now);

        // 2. Right app, but they left the page the step is about.
        if (expected)
        {
            switch (context.TargetOnScreen)
            {
                case true:
                    _targetSeen = true;
                    _targetLostSince = null;
                    break;
                case false when _targetSeen:
                    _targetLostSince ??= now;
                    if (now - _targetLostSince >= TargetLostFor)
                    {
                        _targetSeen = false; // look for it afresh rather than firing again straight away
                        _targetLostSince = null;
                        return Spend(OffPlanVerdict.OffPlan, "target-lost", now);
                    }
                    break;
            }
        }

        // 3. Nothing has happened for a while.
        var idleSince = context.StepStartedAt > _activityAt ? context.StepStartedAt : _activityAt;
        if (now - idleSince >= StuckLimit(context))
        {
            _activityAt = now; // a full new wait before asking again, whatever the budget says
            return Spend(OffPlanVerdict.Stuck, "stuck", now);
        }

        return OffPlanVerdict.OnPlan;
    }

    /// <summary>How long a person may go without visible activity on this step before we offer another way.</summary>
    public static TimeSpan StuckLimit(OffPlanContext context)
    {
        var step = context.Step;
        if (step.DoneWhen.Kind == DoneKind.UserConfirms || step.Action == ActionKind.Type) return StuckWhileTyping;
        if (step.Action == ActionKind.Observe) return StuckWhileObserving;
        return context.TargetOnScreen == true ? StuckWithRing : StuckWithoutRing;
    }

    private OffPlanVerdict Spend(OffPlanVerdict verdict, string reason, DateTimeOffset now)
    {
        while (_replans.Count > 0 && now - _replans.Peek() >= ReplanWindow) _replans.Dequeue();

        var tooSoon = _replans.Count > 0 && now - _replans.Last() < MinGapBetweenReplans;
        if (tooSoon || _replans.Count >= MaxReplansPerWindow) return OffPlanVerdict.OnPlan;

        _replans.Enqueue(now);
        LastReason = reason;
        return verdict;
    }
}

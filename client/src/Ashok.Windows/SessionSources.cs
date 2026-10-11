using Ashok.Core.Capture;
using Ashok.Core.Evaluation;
using Ashok.Core.Session;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Ashok.Windows;

/// <summary>
/// What the session sees: the foreground window and its UNRANKED candidates (after redaction, before the
/// ranker). The ranker drops "redundant" elements such as a heading that repeats a control's name, which is
/// often exactly what a playbook target or doneWhen points at, so it is only for the Phase 4 planner.
/// </summary>
public sealed class CandidateSource(
    IForegroundWindowProvider foreground,
    IElementSource source,
    IReadOnlySet<string>? sensitiveProcesses = null) : ISnapshotSource
{
    public CapturedScreen Capture()
    {
        var window = foreground.Get();
        if (window is null) return new CapturedScreen(new ForegroundState("none", "", 0), []);

        var state = new ForegroundState(window.Process, window.Title, window.Handle.ToInt64());

        // Checked before any UIA call: these windows are never read at all.
        if (Redaction.IsSensitiveProcess(window.Process, sensitiveProcesses))
            return new CapturedScreen(state with { Title = "" }, []);

        return new CapturedScreen(state, Redaction.Apply(source.Read(window.Handle)).Candidates);
    }
}

/// <summary>The element with keyboard focus, for focusOn steps. Applies the same redaction as candidates.</summary>
public sealed class FocusedElementProbe : IFocusProbe, IDisposable
{
    private readonly UIA3Automation _automation = new();

    public FocusedElement? Get()
    {
        AutomationElement? element;
        try
        {
            element = _automation.FocusedElement();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
        if (element is null) return null;

        try
        {
            var bounds = element.Properties.BoundingRectangle.ValueOrDefault;
            var raw = new RawElement(
                element.Properties.ControlType.ValueOrDefault.ToString(),
                element.Properties.Name.ValueOrDefault ?? "",
                element.Properties.AutomationId.ValueOrDefault,
                (int)bounds.X, (int)bounds.Y, Math.Max((int)bounds.Width, 1), Math.Max((int)bounds.Height, 1),
                true, null,
                element.Properties.IsPassword.ValueOrDefault,
                false);

            // Redaction may drop it (passwords, list cells named after their values): then nothing is focused.
            var safe = Redaction.Apply([raw]).Candidates.FirstOrDefault();
            return safe is null ? null : new FocusedElement(safe.Role, safe.Name, safe.AutomationId);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public void Dispose() => _automation.Dispose();
}

/// <summary>
/// The polling fallback the plan calls for: signals "look again" every 250 ms. It does no UI Automation work
/// itself, so a slow window cannot stall it; the runner decides whether a capture is already in flight.
/// </summary>
public sealed class PollingUiWatcher(TimeSpan? interval = null) : IUiWatcher
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    public IDisposable Start(Action onChange)
    {
        var period = interval ?? DefaultInterval;
        return new Timer(_ => onChange(), null, period, period);
    }
}

// OPUS: UI Automation event watcher (plan "Tricky parts", phase 3). UIA events fire on background threads,
// are sometimes dropped, and can deadlock if handled on the UI thread. It should raise the same "look again"
// signal as PollingUiWatcher, only sooner (focus changed, window opened, structure or property changed),
// and keep polling as the safety net for dropped events. Not registered anywhere yet; the app uses
// PollingUiWatcher until this exists.
public sealed class UiaEventWatcher : IUiWatcher
{
    public IDisposable Start(Action onChange) =>
        throw new NotSupportedException("UiaEventWatcher is not implemented yet; use PollingUiWatcher.");
}

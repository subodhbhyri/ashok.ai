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
    public CapturedScreen Capture() => Capture(foreground.Get());

    public CapturedScreen? CaptureWindow(long hwnd)
    {
        var handle = new IntPtr(hwnd);
        return NativeMethods.IsWindow(handle) ? Capture(Win32ForegroundWindowProvider.Describe(handle)) : null;
    }

    private CapturedScreen Capture(ForegroundWindow? window)
    {
        if (window is null) return new CapturedScreen(new ForegroundState("none", "", 0), []);

        var state = new ForegroundState(window.Process, window.Title, window.Handle.ToInt64());

        // Checked before any UIA call: these windows are never read at all.
        if (Redaction.IsSensitiveProcess(window.Process, sensitiveProcesses))
            return new CapturedScreen(state with { Title = "" }, []);

        var redaction = Redaction.Apply(source.Read(window.Handle));
        return new CapturedScreen(state, redaction.Candidates, redaction.Redacted);
    }
}

/// <summary>The element with keyboard focus, for focusOn steps. Applies the same redaction as candidates.</summary>
public sealed class FocusedElementProbe : IFocusProbe, IDisposable
{
    private UIA3Automation _automation = new();
    private readonly CaptureRepairGuard _guard;
    private readonly IDebugLog? _log;

    public FocusedElementProbe(IDebugLog? log = null)
    {
        _log = log;
        _guard = new CaptureRepairGuard(ResetAutomation);
    }

    /// <summary>Bad reads are retried once and repeated ones recreate the automation object; what still fails reads as "nothing focused".</summary>
    public FocusedElement? Get() => _guard.Run<FocusedElement?>(0, GetOnce, swallow: true, whenSwallowed: null);

    private void ResetAutomation(int hresult)
    {
        var old = _automation;
        _automation = new UIA3Automation();
        try { old.Dispose(); } catch (Exception) { } // already broken
        _log?.AutomationReset(DateTimeOffset.UtcNow, FlaUiElementSource.ProcessOf(NativeForeground()), hresult);
    }

    private static IntPtr NativeForeground() => new Win32ForegroundWindowProvider().Get()?.Handle ?? IntPtr.Zero;

    private FocusedElement? GetOnce()
    {
        var element = _automation.FocusedElement();
        if (element is null) return null;

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

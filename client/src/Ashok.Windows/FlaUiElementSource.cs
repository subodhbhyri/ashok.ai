using Ashok.Core.Capture;
using Ashok.Core.Session;
using FlaUI.Core;
using FlaUI.Core.Identifiers;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Ashok.Windows;

/// <summary>
/// Walks a window with one cached UIA request, so there are no per-element cross-process calls.
/// Never reads the Value pattern: field contents are not requested at all.
/// </summary>
public sealed class FlaUiElementSource : IElementSource, IDisposable
{
    private static readonly HashSet<ControlType> Interactive =
    [
        ControlType.Button, ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox,
        ControlType.Edit, ControlType.ListItem, ControlType.TreeItem, ControlType.MenuItem,
        ControlType.TabItem, ControlType.Hyperlink, ControlType.Slider, ControlType.SplitButton,
        ControlType.Text, // playbook doneWhen targets use Text; unnamed Text is skipped below
    ];

    private UIA3Automation _automation = new();
    private readonly CaptureRepairGuard _guard;
    private readonly IDebugLog? _log;
    private IntPtr _lastWindow;

    public FlaUiElementSource(IDebugLog? log = null)
    {
        _log = log;
        _guard = new CaptureRepairGuard(hresult => ResetAutomation(hresult, ProcessOf(_lastWindow)));
    }

    /// <summary>Bad reads are retried once; repeated ones recreate the automation object. All on the capture thread.</summary>
    public IReadOnlyList<RawElement> Read(IntPtr windowHandle)
    {
        _lastWindow = windowHandle;
        return _guard.Run(windowHandle.ToInt64(), () => ReadOnce(windowHandle), list => list.Count == 0);
    }

    private void ResetAutomation(int hresult, string process)
    {
        var old = _automation;
        _automation = new UIA3Automation();
        try { old.Dispose(); } catch (Exception) { } // the old object is already broken; the new one is what matters
        _log?.AutomationReset(DateTimeOffset.UtcNow, process, hresult);
    }

    internal static string ProcessOf(IntPtr window)
    {
        try { return window == IntPtr.Zero ? "-" : Win32ForegroundWindowProvider.Describe(window).Process; }
        catch (Exception) { return "-"; }
    }

    private IReadOnlyList<RawElement> ReadOnce(IntPtr windowHandle)
    {
        var props = _automation.PropertyLibrary.Element;
        var cache = new CacheRequest
        {
            TreeScope = TreeScope.Element,
            AutomationElementMode = AutomationElementMode.Full,
        };
        cache.Add(props.Name);
        cache.Add(props.ControlType);
        cache.Add(props.AutomationId);
        cache.Add(props.BoundingRectangle);
        cache.Add(props.IsEnabled);
        cache.Add(props.IsOffscreen);
        cache.Add(props.IsPassword);
        cache.Add(_automation.PropertyLibrary.Toggle.ToggleState);

        // Filter inside the UIA provider so only visible, relevant elements cross the process boundary.
        var cf = _automation.ConditionFactory;
        ConditionBase condition = new AndCondition(
            new PropertyCondition(props.IsOffscreen, false),
            new OrCondition(Interactive.Select(t => (ConditionBase)cf.ByControlType(t)).ToArray()));

        var toggleProp = _automation.PropertyLibrary.Toggle.ToggleState;
        var result = new List<RawElement>();

        // Cached property reads only hit the cache while the request is active; reading after
        // Dispose would silently turn every property into a live cross-process call.
        using (cache.Activate())
        {
            var root = _automation.FromHandle(windowHandle);
            var all = root.FindAll(TreeScope.Descendants, condition);

            foreach (var e in all)
            {
                var type = e.Properties.ControlType.ValueOrDefault;
                if (!Interactive.Contains(type)) continue;

                var name = e.Properties.Name.ValueOrDefault ?? "";
                if (type == ControlType.Text && name.Length == 0) continue;

                var r = e.Properties.BoundingRectangle.ValueOrDefault;
                result.Add(new RawElement(
                    type.ToString(),
                    name,
                    e.Properties.AutomationId.ValueOrDefault,
                    (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height,
                    e.Properties.IsEnabled.ValueOrDefault,
                    ToggleOf(e, toggleProp),
                    e.Properties.IsPassword.ValueOrDefault,
                    e.Properties.IsOffscreen.ValueOrDefault));
            }
        }
        return result;
    }

    private static string? ToggleOf(AutomationElement e, PropertyId toggleProp)
    {
        if (!e.FrameworkAutomationElement.TryGetPropertyValue<ToggleState>(toggleProp, out var state)) return null;
        return state switch
        {
            ToggleState.On => "on",
            ToggleState.Off => "off",
            ToggleState.Indeterminate => "indeterminate",
            _ => null,
        };
    }

    public void Dispose() => _automation.Dispose();
}

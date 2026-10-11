using System.Diagnostics;
using Ashok.Core.Contracts;
using Ashok.Core.Evaluation;
using Ashok.Core.Intent;
using Ashok.Core.Overlay;
using Ashok.Core.Playbooks;
using Ashok.Core.Resolution;
using Ashok.Core.Session;
using Ashok.Core.Speech;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Xunit.Abstractions;

namespace Ashok.Windows.Tests;

internal sealed class RecordingOverlay : IOverlay
{
    public List<ScreenRect> Shown { get; } = [];

    public bool Pulse { get; set; } = true;

    public void Show(ScreenRect target) => Shown.Add(target);

    public void Hide() { }
}

internal sealed class SilentSpeaker : ISpeaker
{
    public Task SpeakAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Stop() { }
}

internal sealed class RecordingView : ISessionView
{
    public void ShowStep(StepView step) { }

    public void ShowMessage(string text) { }

    public void Clear() { }
}

internal sealed class InlineUi : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>Fails the run if anything asks the planner, which is the only path to the network.</summary>
internal sealed class PlannerTripwire : IPlanner
{
    public int Calls;

    public Task<PlannerResult> PlanAsync(PlanRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        throw new InvalidOperationException("The planner (the only network path) was called during an offline playbook run.");
    }
}

/// <summary>Reads whichever window the scripted user is working in, regardless of which one has real focus.</summary>
internal sealed class DeskForeground : IForegroundWindowProvider
{
    private long _handle;

    public IntPtr Current
    {
        get => new(Interlocked.Read(ref _handle));
        set => Interlocked.Exchange(ref _handle, value.ToInt64());
    }

    public ForegroundWindow? Get() => Current == IntPtr.Zero ? null : Win32ForegroundWindowProvider.Describe(Current);
}

/// <summary>
/// Plays the user. Launching windows, invoking controls and clicking are test code standing in for a person;
/// the product itself only points and speaks. It acts through UI Automation where it can, so it does not depend
/// on which window has focus.
/// </summary>
internal sealed class ScriptedUser(UIA3Automation automation, DeskForeground desk, ITestOutputHelper output)
{
    public List<IntPtr> Opened { get; } = [];

    public DeskForeground Desk => desk;

    public IntPtr WaitForWindow(Func<string, string, string, bool> match, bool existingOk = false, int seconds = 15)
    {
        var before = TestNative.TopLevelWindows().Select(w => w.Handle).ToHashSet();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var found = TestNative.TopLevelWindows()
                .Where(w => (existingOk || !before.Contains(w.Handle)) && match(w.Class, w.Title, ProcessOf(w.Handle)))
                .Select(w => w.Handle).FirstOrDefault();
            if (found != IntPtr.Zero) return found;
            Thread.Sleep(200);
        }
        throw new TimeoutException("The window did not appear.");
    }

    public IntPtr Launch(Action start, Func<string, string, string, bool> match, bool allowExisting = true)
    {
        var before = TestNative.TopLevelWindows().Select(w => w.Handle).ToHashSet();
        start();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var found = TestNative.TopLevelWindows()
                .Where(w => !before.Contains(w.Handle) && match(w.Class, w.Title, ProcessOf(w.Handle)))
                .Select(w => w.Handle).FirstOrDefault();
            if (found != IntPtr.Zero)
            {
                Opened.Add(found);
                Thread.Sleep(1500); // let the window finish loading
                desk.Current = found;
                return found;
            }
            Thread.Sleep(250);
        }

        if (allowExisting && TestNative.TopLevelWindows().FirstOrDefault(w => match(w.Class, w.Title, ProcessOf(w.Handle))) is { } existing &&
            existing.Handle != IntPtr.Zero)
        {
            output.WriteLine("    (no new window appeared; using the one already open)");
            TestNative.RestoreIfMinimized(existing.Handle);
            desk.Current = existing.Handle;
            return existing.Handle;
        }
        throw new TimeoutException("The window did not appear.");
    }

    public static string ProcessOf(IntPtr hwnd) => Win32ForegroundWindowProvider.Describe(hwnd).Process;

    public AutomationElement Root => automation.FromHandle(desk.Current);

    /// <summary>
    /// The live element behind a candidate. The screen the session saw can be a moment old (a Settings page
    /// slides in), so exact bounds are preferred but role plus id or name is enough.
    /// </summary>
    public AutomationElement? Locate(UiElement candidate)
    {
        // Ask UI Automation to filter (a page with hundreds of rows times out on a full walk).
        var cf = automation.ConditionFactory;
        var byRole = cf.ByControlType(Enum.Parse<FlaUI.Core.Definitions.ControlType>(candidate.Role));
        var narrowed = candidate.AutomationId is { } id ? byRole.And(cf.ByAutomationId(id)) : byRole.And(cf.ByName(candidate.Name));
        var matches = Root.FindAllDescendants(narrowed).Where(e => SafeOnScreen(e)).ToList();

        return matches.FirstOrDefault(e => SameBounds(e, candidate)) ?? matches.FirstOrDefault();
    }

    private static bool SafeOnScreen(AutomationElement e)
    {
        try { return !e.IsOffscreen; }
        catch (Exception) { return false; }
    }

    private static bool SameBounds(AutomationElement e, UiElement candidate)
    {
        try
        {
            var b = e.BoundingRectangle;
            return (int)b.X == candidate.Bounds[0] && (int)b.Y == candidate.Bounds[1] &&
                   (int)b.Width == candidate.Bounds[2] && (int)b.Height == candidate.Bounds[3];
        }
        catch (Exception)
        {
            return false;
        }
    }

    public AutomationElement? FindById(string automationId) =>
        Root.FindFirstDescendant(automation.ConditionFactory.ByAutomationId(automationId));

    public AutomationElement? FindByName(string name) =>
        Root.FindFirstDescendant(automation.ConditionFactory.ByName(name));

    /// <summary>Does what a person would do to a control: select, press, toggle or click it.</summary>
    public void Use(UiElement candidate, ActionKind action)
    {
        var element = Locate(candidate) ?? throw new InvalidOperationException($"Could not find {candidate.Role} '{candidate.Name}' on screen.");
        Use(element, action, candidate.Role);
    }

    public static void Use(AutomationElement element, ActionKind action, string role)
    {
        if (role == "Slider") return; // never nudge a real volume or brightness slider
        if (action == ActionKind.DoubleClick) { Mouse.DoubleClick(element.GetClickablePoint()); return; }
        if (action == ActionKind.Click)
        {
            if (element.Patterns.Toggle.IsSupported) { element.Patterns.Toggle.Pattern.Toggle(); return; }
            if (element.Patterns.SelectionItem.IsSupported) { element.Patterns.SelectionItem.Pattern.Select(); return; }
            if (element.Patterns.Invoke.IsSupported) { element.Patterns.Invoke.Pattern.Invoke(); return; }
        }
        Mouse.Click(element.GetClickablePoint());
    }
}

/// <summary>What a per-step hook can use.</summary>
internal sealed record HookContext(
    ScriptedUser User, CapturedScreen Screen, PlanStep Step, Action ConfirmStep, Action<Action> OnCleanup);

/// <summary>
/// Every playbook, played end to end on this Windows 11 machine with no planner and no network. Steps that would
/// change real system state (pairing, uninstalling, saving PATH, changing the scale, toggling Bluetooth) stop one
/// step short: the session must be guiding that step, with the ring on the right control.
/// </summary>
[Trait("Category", "Desktop")]
public class PlaybookEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private sealed record Script(
        string? StopBefore = null, Dictionary<string, Action<HookContext>>? Hooks = null,
        Action<ScriptedUser, ScreenRect>? VerifyRing = null);

    private static readonly string PlaybooksDir = FindPlaybooks();

    private readonly UIA3Automation _automation = new();
    private readonly List<Action> _cleanup = [];

    public void Dispose()
    {
        foreach (var undo in Enumerable.Reverse(_cleanup))
        {
            try { undo(); } catch (Exception e) { output.WriteLine($"cleanup: {e.Message}"); }
        }
        _automation.Dispose();
    }

    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (var id in new[]
                 {
                     "find-downloads", "open-downloaded-file", "take-screenshot", "change-volume", "connect-wifi", "bluetooth-on",
                     "pair-device", "change-display-scaling", "uninstall-app", "edit-path",
                 })
            data.Add(id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task Playbook_runs_end_to_end_offline(string id)
    {
        var store = PlaybookStore.LoadDirectory(PlaybooksDir, "win11");
        var playbook = store.Find(id)!;

        var desk = new DeskForeground();
        var resolver = new TargetResolver();
        var user = new ScriptedUser(_automation, desk, output);
        var overlay = new RecordingOverlay();
        var planner = new PlannerTripwire();
        var script = ScriptFor(id, user);

        using var source = new FlaUiElementSource();
        var screens = new CandidateSource(desk, source);
        var session = new GuidanceSession(
            new EmbeddingIntentMatcher(new HashingEmbedder(), store.All, threshold: 0.35),
            store, planner, new NaiveOffPlanDetector(), resolver, new DoneEvaluator(resolver),
            overlay, new SilentSpeaker(), new RecordingView(), new InlineUi(), TimeProvider.System, "no-such-process");
        using var focus = new FocusedElementProbe();
        using var runner = new SessionRunner(session, screens, focus, new PollingUiWatcher(TimeSpan.FromMilliseconds(150)));
        runner.Error += e => output.WriteLine($"    runner error: {e.Message}");
        _cleanup.Add(() => { foreach (var h in user.Opened) TestNative.Close(h); });

        var clock = Stopwatch.StartNew();
        var outcome = await Drive(id, playbook, script, runner, session, user, screens, overlay, resolver);

        output.WriteLine($"RESULT {id}: {outcome} in {clock.Elapsed.TotalSeconds:F1}s, {playbook.Steps.Count} steps, planner calls {planner.Calls}");
        Assert.Equal(0, planner.Calls);
    }

    private async Task<string> Drive(
        string id, Playbook playbook, Script script, SessionRunner runner, GuidanceSession session,
        ScriptedUser user, CandidateSource screens, RecordingOverlay overlay, TargetResolver resolver)
    {
        await runner.AskAsync(playbook.Examples[0], 0);

        var performed = new HashSet<string>();
        var lastStep = -1;
        var stepClock = Stopwatch.StartNew();
        var overall = Stopwatch.StartNew();

        while (true)
        {
            var state = session.State;
            if (state.Phase == SessionPhase.Done) return $"completed all {playbook.Steps.Count} steps";
            Assert.True(state.Phase == SessionPhase.Guiding, $"{id}: session ended as {state.Phase} after step {lastStep + 1}. {Describe(runner)}");
            Assert.True(overall.Elapsed < TimeSpan.FromMinutes(3), $"{id}: took over three minutes. {Describe(runner)}");

            var step = playbook.Steps[state.StepIndex];
            if (state.StepIndex != lastStep)
            {
                lastStep = state.StepIndex;
                stepClock.Restart();
                output.WriteLine($"  {step.Id} ({step.Action}): {step.Say}");
            }
            Assert.True(stepClock.Elapsed < TimeSpan.FromSeconds(25), $"{id}/{step.Id}: no progress for 25 s. {Describe(runner, step)}");

            if (step.Id == script.StopBefore)
            {
                await Task.Delay(800); // let the ring settle
                if (step.Target is not null) AssertRingOnTarget(id, step, runner, overlay, resolver);
                if (step.Target is not null) script.VerifyRing?.Invoke(user, overlay.Shown[^1]);
                return step.Target is not null
                    ? $"stopped before {step.Id}, ring on the target"
                    : $"stopped before {step.Id} (a step with nothing to point at)";
            }

            if (performed.Contains(step.Id)) { await Task.Delay(250); continue; }
            if (Perform(step, script, user, screens, runner, resolver)) performed.Add(step.Id);
            await Task.Delay(250);
        }
    }

    /// <summary>Returns false when the step cannot be done yet (the control is not on screen) so it is tried again.</summary>
    private bool Perform(PlanStep step, Script script, ScriptedUser user, CandidateSource screens, SessionRunner runner, TargetResolver resolver)
    {
        // Never capture here: the runner owns capturing, and captures must not overlap.
        var screen = runner.LastScreen;
        if (screen is null) return false;
        void Confirm() => runner.Confirm();

        if (script.Hooks is not null && script.Hooks.TryGetValue(step.Id, out var hook))
        {
            hook(new HookContext(user, screen, step, Confirm, _cleanup.Add));
            return true;
        }

        switch (step.Action)
        {
            case ActionKind.PressKeys:
                PressKeys(step.Keys!, user);
                break;

            case ActionKind.Click or ActionKind.DoubleClick or ActionKind.RightClick or ActionKind.Type when step.Target is { } target:
                var found = resolver.Best(target, screen.Candidates);
                if (found is null)
                {
                    output.WriteLine($"    (waiting: {target.Role} '{target.Name}' is not on screen yet)");
                    return false;
                }
                output.WriteLine($"    {step.Action} {found.Element.Role} '{found.Element.Name}' [{found.How}]");
                try
                {
                    user.Use(found.Element, step.Action);
                }
                catch (InvalidOperationException e)
                {
                    output.WriteLine($"    (not clickable yet: {e.Message})");
                    return false;
                }
                break;
        }

        if (step.DoneWhen.Kind == DoneKind.UserConfirms)
        {
            output.WriteLine("    press Done");
            Confirm();
        }
        return true;
    }

    // The scripted user opens things the way the keys would, without sending keystrokes to the real desktop.
    private void PressKeys(string keys, ScriptedUser user)
    {
        switch (keys)
        {
            case "Win+I":
                user.Launch(() => Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true }),
                    (cls, title, _) => cls == "ApplicationFrameWindow" && title == "Settings");
                break;
            case "Win+E":
                // Win+E opens wherever the user set File Explorer to open; on this machine that is Downloads, which
                // would satisfy step 2 without a click. This PC has the same sidebar and does not.
                user.Launch(() => Process.Start("explorer.exe", "shell:MyComputerFolder"),
                    (cls, _, _) => cls == "CabinetWClass", allowExisting: false);
                break;
            case "Win+R":
                user.Launch(() => Process.Start("rundll32.exe", "shell32.dll,#61"), (cls, title, _) => cls == "#32770" && title == "Run");
                break;
            case "Win+Shift+S":
                _cleanup.Add(TestNative.TapEscape);
                user.Launch(() => Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }),
                    (_, _, process) => process is "SnippingTool" or "ScreenClippingHost");
                break;
            default:
                throw new NotSupportedException($"No scripted equivalent for {keys}.");
        }
    }

    private static void AssertRingOnTarget(string id, PlanStep step, SessionRunner runner, RecordingOverlay overlay, TargetResolver resolver)
    {
        var expected = resolver.Best(step.Target!, runner.LastScreen!.Candidates)
            ?? throw new Xunit.Sdk.XunitException($"{id}/{step.Id}: target {step.Target!.Role} '{step.Target.Name}' is not on screen.");
        var rect = ScreenRect.FromBounds(expected.Element.Bounds);

        Assert.True(overlay.Shown.Count > 0 && overlay.Shown[^1] == rect,
            $"{id}/{step.Id}: the ring should be on {rect} but the last ring was {(overlay.Shown.Count > 0 ? overlay.Shown[^1].ToString() : "never shown")}.");
    }

    private static string Describe(SessionRunner runner, PlanStep? step = null)
    {
        var s = runner.LastScreen ?? new CapturedScreen(new ForegroundState("none", "", 0), []);
        var wanted = new[] { step?.Target?.Name, step?.DoneWhen.Target?.Name }.Where(n => n is not null).ToList();
        var related = s.Candidates.Where(c => wanted.Any(n => c.Name.Contains(n!, StringComparison.OrdinalIgnoreCase)))
            .Select(c => $"{c.Role}:'{c.Name}' id={c.AutomationId}");
        var first = string.Join(" | ", s.Candidates.Take(12).Select(c => $"{c.Role}:{c.Name}"));
        return $"Foreground {s.Foreground.Process} '{s.Foreground.Title}', {s.Candidates.Count} candidates. " +
               $"Step wants {string.Join(", ", wanted)}; related on screen: [{string.Join("; ", related)}]. First: {first}";
    }

    // ---- the per-playbook scripts --------------------------------------------------------------

    private Script ScriptFor(string id, ScriptedUser user) => id switch
    {
        // Safe to run to the end: they only open windows and navigate.
        "find-downloads" => new(),
        "take-screenshot" => new(),

        // The volume slider is pointed at but never touched; the scripted user just says Done.
        "change-volume" => new(Hooks: new() { ["s4"] = c => c.ConfirmStep() }),

        // Stops at the Bluetooth switch. If it is already On (as here) the playbook simply completes.
        "bluetooth-on" => new("s3"),

        // Opens the Add a device dialog and starts the scan, then waits at "pick your device".
        "pair-device" => new("s5", new()
        {
            ["s4"] = c =>
            {
                ClickById(c.User, "BluetoothDevicesButton");
                c.OnCleanup(() => ClickById(c.User, "ButtonClose"));
            },
        }),

        // View only: clicks "Show available networks" with the mouse, then stops before choosing a network.
        "connect-wifi" => new("s6"),

        // Scrolls Scale into view and stops before opening it.
        "change-display-scaling" => new("s5", new()
        {
            ["s4"] = c => c.User.FindById("SystemSettings_Display_Scaling_ItemSizeOverride_ComboBox")?.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(),
        }),

        // Opens the first app's menu and stops before Uninstall.
        "uninstall-app" => new("s5", new()
        {
            ["s4"] = c => c.User.Root.FindAllDescendants(_automation.ConditionFactory.ByAutomationId("AppOptionsButton"))
                .FirstOrDefault(e => !e.IsOffscreen)?.Patterns.Invoke.PatternOrDefault?.Invoke(),
        }),

        // Opens Environment Variables and the Path editor, adds an empty row, and stops before OK (the save).
        // Two OK buttons are on screen (the editor's and the window behind it); the ring must be on the editor's.
        "edit-path" => new("s7", VerifyRing: (u, ring) =>
        {
            var editor = u.Root.FindAllChildren().FirstOrDefault(e => e.Name == "Edit environment variable")
                ?? throw new Xunit.Sdk.XunitException("The Path editor dialog is not open.");
            var box = editor.BoundingRectangle;
            Assert.True(ring.X >= box.X && ring.Y >= box.Y && ring.Right <= box.X + box.Width && ring.Bottom <= box.Y + box.Height,
                $"The ring {ring} is not inside the Path editor dialog {box}: it landed on the OK of the window behind it.");
        }, Hooks: new()
        {
            ["s2"] = c =>
            {
                var box = c.User.FindById("1001") ?? throw new InvalidOperationException("The Run box is not open.");
                box.Patterns.Value.Pattern.SetValue("rundll32 sysdm.cpl,EditEnvironmentVariables");
                c.User.FindById("1")!.Patterns.Invoke.Pattern.Invoke();   // OK, same as pressing Enter
                var env = c.User.WaitForWindow((cls, title, _) => cls == "#32770" && title == "Environment Variables");
                c.User.Opened.Add(env);
                c.User.Desk.Current = env;
                c.OnCleanup(() => CancelDialogs(c.User, env));
            },
        }),

        // Opens a temporary file this test creates in Downloads and deletes afterwards.
        "open-downloaded-file" => new(Hooks: new() { ["s3"] = OpenTemporaryDownload }),

        _ => throw new ArgumentException(id),
    };

    private static void ClickById(ScriptedUser user, string id)
    {
        var element = user.FindById(id) ?? throw new InvalidOperationException($"Could not find the control with id {id}.");
        element.Patterns.Invoke.Pattern.Invoke();
    }

    private static void CancelDialogs(ScriptedUser user, IntPtr env)
    {
        user.Desk.Current = env;
        for (var i = 0; i < 2; i++)
        {
            var cancel = user.Root.FindAllDescendants().LastOrDefault(e => e.AutomationId == "2" && !e.IsOffscreen);
            cancel?.Patterns.Invoke.PatternOrDefault?.Invoke();
            Thread.Sleep(500);
        }
    }

    private static void OpenTemporaryDownload(HookContext c)
    {
        var name = $"ashok-e2e-{Guid.NewGuid().ToString("N")[..8]}";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", name + ".txt");
        File.WriteAllText(path, "Created by the Ashok end-to-end test; safe to delete.");
        c.OnCleanup(() => File.Delete(path));

        // What a double-click does. (Explorer's file list is virtualized, so a brand-new file is not always
        // among the rows UI Automation can see; the step only needs the file to open.)
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

        var editor = c.User.WaitForWindow((_, title, _) => title.Contains(name), existingOk: true, seconds: 20);
        c.OnCleanup(() => TestNative.Close(editor));
        c.ConfirmStep();
    }

    private static string FindPlaybooks()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "playbooks")) && File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return Path.Combine(dir.FullName, "playbooks");
        throw new DirectoryNotFoundException("playbooks/ not found");
    }
}

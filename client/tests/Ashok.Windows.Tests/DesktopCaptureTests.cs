using System.Diagnostics;
using Ashok.Core.Capture;
using Ashok.Core.Contracts;
using Xunit.Abstractions;

namespace Ashok.Windows.Tests;

/// <summary>Real-window checks. Local only: excluded from CI with --filter "Category!=Desktop".</summary>
[Trait("Category", "Desktop")]
public class DesktopCaptureTests(ITestOutputHelper output) : IDisposable
{
    private readonly FlaUiElementSource _source = new();
    private readonly List<IntPtr> _opened = [];

    public void Dispose()
    {
        foreach (var h in _opened) TestNative.Close(h);
        _source.Dispose();
    }

    private UiaCapture CaptureFor(IntPtr hwnd) =>
        new(new FixedForegroundProvider(Win32ForegroundWindowProvider.Describe(hwnd)), _source, new HeuristicRanker());

    /// <summary>Launches something and returns the handle of the new top-level window it created.</summary>
    private IntPtr Launch(Action start, Func<string, string, bool> match)
    {
        var before = TestNative.TopLevelWindows().Select(w => w.Handle).ToHashSet();
        start();
        var deadline = DateTime.UtcNow.AddSeconds(15); // the same wait the end-to-end runner gives a new window
        while (DateTime.UtcNow < deadline)
        {
            var found = TestNative.TopLevelWindows()
                .Where(w => !before.Contains(w.Handle) && match(w.Class, w.Title)).Select(w => w.Handle).ToList();
            if (found.Count > 0)
            {
                _opened.Add(found[0]);
                Restore(found[0]);
                Thread.Sleep(2000); // let the UI finish loading
                return found[0];
            }
            Thread.Sleep(250);
        }

        // Some apps reuse an already-open window (Settings, Explorer tabs). Use it, but never close it.
        var existing = TestNative.TopLevelWindows().Where(w => match(w.Class, w.Title)).Select(w => w.Handle).ToList();
        if (existing.Count > 0)
        {
            output.WriteLine("No new window appeared; using an already-open matching window.");
            Restore(existing[0]);
            return existing[0];
        }
        output.WriteLine("Window did not appear. Every top-level window right now:");
        foreach (var line in TestNative.DescribeAllWindows()) output.WriteLine("  " + line);
        throw new TimeoutException("Window did not appear.");
    }

    // Harness-only: an earlier run can leave a window minimized, which makes every element offscreen.
    private static void Restore(IntPtr hwnd) => TestNative.RestoreIfMinimized(hwnd);

    private void AssertWithinCriteria(string label, IntPtr hwnd)
    {
        var capture = CaptureFor(hwnd);
        capture.Capture(); // warm-up: first UIA call pays one-time initialisation

        var sw = Stopwatch.StartNew();
        var snapshot = capture.Capture();
        var ms = sw.ElapsedMilliseconds;
        var (_, candidates, _) = capture.CaptureCandidates();

        output.WriteLine($"{label}: {snapshot.Elements.Count} elements ({candidates.Count} candidates), " +
                         $"{snapshot.Redacted} redacted, truncated={snapshot.Truncated}, {ms} ms warm");

        Assert.True(snapshot.Elements.Count <= 150);
        Assert.True(ms < 300, $"{label} capture took {ms} ms");
        Assert.NotEmpty(snapshot.Elements);
    }

    [Fact]
    public void Settings() =>
        AssertWithinCriteria("Settings",
            Launch(() => Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true }),
                (cls, title) => cls == "ApplicationFrameWindow" && title == "Settings"));

    [Fact]
    public void File_Explorer() =>
        AssertWithinCriteria("File Explorer",
            Launch(() => Process.Start("explorer.exe", Environment.SystemDirectory),
                (cls, title) => cls == "CabinetWClass" && title.StartsWith("System32")));

    [Fact]
    public void Control_Panel() =>
        AssertWithinCriteria("Control Panel",
            Launch(() => Process.Start("control.exe"),
                (cls, title) => cls == "CabinetWClass" && title.Contains("Control Panel")));

    [Fact]
    public void Chrome()
    {
        var chrome = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\Application\chrome.exe"),
        }.FirstOrDefault(File.Exists);
        if (chrome is null)
        {
            output.WriteLine("Chrome is not installed; not checked.");
            return;
        }

        var profile = Path.Combine(Path.GetTempPath(), "ashok-chrome-" + Guid.NewGuid().ToString("N"));
        AssertWithinCriteria("Chrome",
            Launch(() => Process.Start(chrome, $"--user-data-dir=\"{profile}\" --no-first-run --new-window about:blank"),
                (cls, _) => cls == "Chrome_WidgetWin_1"));
    }

    [Fact]
    public void Edit_values_never_leak_and_password_fields_are_dropped()
    {
        using var probe = new WpfProbeWindow();
        var capture = CaptureFor(probe.Handle);

        var snapshot = capture.Capture();
        var json = SnapshotJson.Serialize(snapshot);
        var (_, candidates, _) = capture.CaptureCandidates();
        var rawJson = SnapshotJson.Serialize(candidates);

        Assert.DoesNotContain(WpfProbeWindow.EditValue, json + rawJson);
        Assert.DoesNotContain(WpfProbeWindow.PasswordValue, json + rawJson);
        Assert.Contains(snapshot.Elements, e => e.Role == "Edit" && e.Name == "Email");
        Assert.DoesNotContain(snapshot.Elements, e => e.Name == "Secret");
        Assert.True(snapshot.Redacted >= 1);
    }

    [Fact]
    public void Process_is_PerMonitorV2_aware()
    {
        Assert.True(TestNative.IsPerMonitorV2(),
            $"PerMonitorV2 not in effect (SetProcessDpiAwarenessContext returned {TestNative.DpiAwarenessRequestSucceeded}).");
    }

    [Fact]
    public void Bounds_are_physical_pixels_at_the_current_scale()
    {
        Assert.True(TestNative.IsPerMonitorV2());
        using var probe = new WpfProbeWindow();
        var snapshot = CaptureFor(probe.Handle).Capture();
        var (wpfWidth, wpfHeight, scale) = probe.ButtonPhysicalSize();
        var dpiForWindow = TestNative.GetDpiForWindow(probe.Handle) / 96.0;
        output.WriteLine($"Scale {scale:P0} (GetDpiForWindow {dpiForWindow:P0}); WPF button {wpfWidth:F1}x{wpfHeight:F1} px");

        Assert.Equal(dpiForWindow, scale, 2);

        // The button is 120x40 DIPs, so UIA must report 120*scale x 40*scale physical pixels. A DPI-virtualized
        // answer would be 120x40 regardless of scale.
        var button = Assert.Single(snapshot.Elements, e => e.Role == "Button" && e.Name == "Probe");
        Assert.InRange(button.Bounds[2], wpfWidth - 1.5, wpfWidth + 1.5);
        Assert.InRange(button.Bounds[3], wpfHeight - 1.5, wpfHeight + 1.5);

        // And every element must sit inside the window's physical rectangle.
        TestNative.GetWindowRect(probe.Handle, out var win);
        foreach (var e in snapshot.Elements)
        {
            Assert.InRange(e.Bounds[0], win.Left - 2, win.Right + 2);
            Assert.InRange(e.Bounds[1], win.Top - 2, win.Bottom + 2);
            Assert.InRange(e.Bounds[0] + e.Bounds[2], win.Left - 2, win.Right + 2);
            Assert.InRange(e.Bounds[1] + e.Bounds[3], win.Top - 2, win.Bottom + 2);
        }
    }
}

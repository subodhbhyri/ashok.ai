using System.Diagnostics;
using Xunit.Abstractions;

namespace Ashok.Windows.Tests;

/// <summary>
/// Real-window check. Local only: excluded from CI with --filter "Category!=Desktop".
/// The probe window runs in a separate process on purpose: the watcher skips events from its own process.
/// </summary>
[Trait("Category", "Desktop")]
public class WinEventWatcherTests(ITestOutputHelper output) : IDisposable
{
    private const string Title = "Ashok watcher probe";

    private readonly string _trigger = Path.Combine(Path.GetTempPath(), $"ashok-watcher-{Guid.NewGuid():N}.trigger");
    private string _warmup => _trigger + ".warmup";
    private Process? _child;

    public void Dispose()
    {
        try { if (_child is { HasExited: false }) _child.Kill(true); } catch (InvalidOperationException) { }
        _child?.Dispose();
        foreach (var file in new[] { _warmup, _trigger }) if (File.Exists(file)) File.Delete(file);
    }

    // A WPF window that renames its button when the warm-up file appears, and again when the trigger file appears.
    private string Script() => $$"""
        Add-Type -AssemblyName PresentationFramework
        $b = New-Object System.Windows.Controls.Button
        $b.Content = 'Before'
        $w = New-Object System.Windows.Window
        $w.Title = '{{Title}}'; $w.Width = 300; $w.Height = 200; $w.Topmost = $true
        $w.WindowStartupLocation = 'CenterScreen'; $w.Content = $b
        $t = New-Object System.Windows.Threading.DispatcherTimer
        $t.Interval = [TimeSpan]::FromMilliseconds(20)
        $warm = $false
        $t.Add_Tick({
          if (-not $warm -and (Test-Path -LiteralPath '{{_warmup}}')) { $warm = $true; $b.Content = 'Warm' }
          if (Test-Path -LiteralPath '{{_trigger}}') { $b.Content = 'After'; $t.Stop() }
        })
        $w.Add_ContentRendered({ $w.Activate(); $t.Start() })
        [void]$w.ShowDialog()
        """;

    [Fact]
    public void Renaming_a_button_in_another_process_signals_within_1000_ms()
    {
        var script = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(Script()));
        _child = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -STA -EncodedCommand {script}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var hwnd = IntPtr.Zero;
        SpinWait.SpinUntil(() => (hwnd = TestNative.TopLevelWindows().FirstOrDefault(w => w.Title == Title).Handle) != IntPtr.Zero, TimeSpan.FromSeconds(20));
        Assert.NotEqual(IntPtr.Zero, hwnd);

        TestNative.TapAlt(); // lets the next foreground request through
        TestNative.BringToFront(hwnd);
        Thread.Sleep(1500);
        Assert.True(SpinWait.SpinUntil(() => TestNative.GetForegroundWindow() == hwnd, TimeSpan.FromSeconds(5)),
            "The probe window did not become the foreground window; the watcher only listens to the window in front.");

        var fired = new ManualResetEventSlim();
        var signals = 0;
        using var subscription = new WinEventWatcher(safetyPoll: TimeSpan.FromSeconds(10)).Start(() =>
        {
            Interlocked.Increment(ref signals);
            fired.Set();
        });

        // A warm-up rename first: it proves the hooks are live and absorbs first-event costs (the rest of
        // the window's start-up noise settles too). Only the second rename is timed.
        File.WriteAllText(_warmup, "go");
        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "The warm-up rename produced no signal at all.");
        Thread.Sleep(1500);
        fired.Reset();
        var before = signals;

        var clock = Stopwatch.StartNew();
        File.WriteAllText(_trigger, "go");
        var signalled = fired.Wait(TimeSpan.FromSeconds(3));
        var elapsed = clock.ElapsedMilliseconds;

        output.WriteLine($"signal after {elapsed} ms (signals before: {before}, after: {signals})");
        Assert.True(signalled, "No signal arrived (the 10 s safety poll was not allowed to rescue it).");
        Assert.True(elapsed <= 1000, $"The signal took {elapsed} ms.");
    }
}

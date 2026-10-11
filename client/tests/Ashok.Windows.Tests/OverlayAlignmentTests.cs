using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using Ashok.Core.Capture;
using Ashok.Core.Overlay;
using Ashok.UI.Overlay;
using Xunit.Abstractions;

namespace Ashok.Windows.Tests;

/// <summary>
/// Measures the overlay ring against the screen itself: a probe window is placed on each monitor, its
/// button is located through UI Automation (physical pixels), the overlay is asked to ring it, and the
/// ring's position is read back from a screen capture. Annotated PNGs go to %TEMP%\ashok-overlay.
/// Local only: it puts windows on every monitor and sends one synthetic click per monitor.
/// </summary>
[Trait("Category", "Desktop")]
public class OverlayAlignmentTests(ITestOutputHelper output)
{
    private const int TolerancePx = 4;
    private const int ColorTolerance = 40;

    private sealed record Row(
        string Monitor, int Scale, ScreenRect Target, ScreenRect Expected, ScreenRect? Measured,
        int[]? EdgeErrors, bool Aligned, bool ClickThrough, bool NoFocus, string Image, string? FoundElsewhere);

    [Fact]
    public void Ring_lands_within_4px_clicks_pass_through_and_focus_is_untouched_on_every_monitor()
    {
        var monitors = TestNative.Monitors().OrderByDescending(m => m.Primary).ToList();
        output.WriteLine($"{monitors.Count} monitor(s): " + string.Join("; ", monitors.Select(m =>
            $"{m.Device}{(m.Primary ? " (primary)" : "")} {m.Bounds.Right - m.Bounds.Left}x{m.Bounds.Bottom - m.Bounds.Top} " +
            $"at ({m.Bounds.Left},{m.Bounds.Top}) {m.ScalePercent}%")));
        Assert.True(monitors.Count >= 2, "The check needs the laptop and at least one external monitor.");

        var outDir = Path.Combine(Path.GetTempPath(), "ashok-overlay");
        Directory.CreateDirectory(outDir);
        foreach (var old in Directory.GetFiles(outDir, "*.png")) File.Delete(old);

        // One overlay for the whole run, visited in the order laptop -> externals -> laptop again,
        // so moves between monitors with different scales are exercised, not just fresh windows.
        var order = monitors.Append(monitors[0]).ToList();

        var rows = new List<Row>();
        using var overlayThread = new StaThread();
        var overlay = overlayThread.Invoke(() => new OverlayWindow());
        using var source = new FlaUiElementSource();
        try
        {
            foreach (var monitor in order)
                rows.Add(Measure(monitor, overlay, overlayThread, source, outDir, rows.Count));
        }
        finally
        {
            overlayThread.Invoke(() => overlay.Close());
        }

        output.WriteLine(Table(rows));
        output.WriteLine($"Overlay evidence images: {outDir}");

        var scales = rows.Select(r => r.Scale).Distinct().Order().ToList();
        output.WriteLine($"Scales exercised: {string.Join("%, ", scales)}%");

        var failures = rows.Where(r => !(r.Aligned && r.ClickThrough && r.NoFocus)).ToList();
        Assert.True(failures.Count == 0,
            $"{failures.Count} of {rows.Count} overlay checks failed (images in {outDir}):\n" + Table(failures));
    }

    private Row Measure(
        TestNative.MonitorDescription monitor, OverlayWindow overlay, StaThread overlayThread,
        FlaUiElementSource source, string outDir, int visit)
    {
        using var probe = new WpfProbeWindow();
        PlaceOnMonitor(probe.Handle, monitor);

        // The target comes from the real capture path, in physical pixels.
        var capture = new UiaCapture(
            new FixedForegroundProvider(Win32ForegroundWindowProvider.Describe(probe.Handle)),
            source, new HeuristicRanker());
        var button = capture.Capture().Elements.Single(e => e.Role == "Button" && e.Name == "Probe");
        var target = ScreenRect.FromBounds(button.Bounds);
        var expected = OverlayGeometry.OuterBox(target);

        var foregroundBefore = TestNative.GetForegroundWindow();
        overlayThread.Invoke(() =>
        {
            overlay.Pulse = false;
            overlay.Show(target);
        });
        Thread.Sleep(800); // let the compositor draw it

        var foregroundAfter = TestNative.GetForegroundWindow();
        var overlayHandle = overlay.Handle;
        var exStyle = TestNative.ExStyle(overlayHandle);
        var noFocus = foregroundBefore == foregroundAfter && (exStyle & TestNative.WS_EX_NOACTIVATE) != 0;

        if (!noFocus)
        {
            string Describe(IntPtr h) => h == IntPtr.Zero ? "none" : $"{h} \"{Win32ForegroundWindowProvider.Describe(h).Title}\"";
            output.WriteLine($"{monitor.Device}: foreground {Describe(foregroundBefore)} -> {Describe(foregroundAfter)}; " +
                             $"overlay {overlayHandle}, ex-style 0x{exStyle:X}, probe {probe.Handle}");
        }

        var shot = Screenshot(monitor.Bounds);
        var measured = FindRing(shot, monitor.Bounds);
        var errors = measured is { } m
            ? new[]
            {
                Math.Abs(m.X - expected.X), Math.Abs(m.Y - expected.Y),
                Math.Abs(m.Right - expected.Right), Math.Abs(m.Bottom - expected.Bottom),
            }
            : null;
        // If it is not on its own monitor, say where it did land.
        string? elsewhere = null;
        if (measured is null)
        {
            foreach (var other in TestNative.Monitors().Where(o => o.Device != monitor.Device))
            {
                using var otherShot = Screenshot(other.Bounds);
                if (FindRing(otherShot, other.Bounds) is { } at)
                {
                    elsewhere = $"on {Sanitize(other.Device)} ({other.ScalePercent}%) at {at.X},{at.Y} {at.Width}x{at.Height}";
                    break;
                }
            }
        }
        var aligned = errors is not null && errors.All(e => e <= TolerancePx);

        var image = Path.Combine(outDir, $"{Sanitize(monitor.Device)}-{monitor.ScalePercent}" +
                                         $"{(visit >= TestNative.Monitors().Count ? "-revisit" : "")}.png");
        SaveEvidence(shot, monitor.Bounds, expected, image);

        // Click the middle of the probe button, which sits under the overlay's transparent interior.
        var clicksBefore = probe.ClickCount;
        TestNative.Click(target.X + target.Width / 2, target.Y + target.Height / 2);
        Thread.Sleep(300);
        // Transparent pixels pass clicks anyway, so also hit-test a point on the ring stroke itself:
        // the window found there must not be the overlay.
        var stroke = new TestNative.Point { X = target.X + target.Width / 2, Y = expected.Y + OverlayGeometry.RingThicknessPx / 2 };
        var hit = TestNative.RootWindowAt(stroke);
        var clickThrough = probe.ClickCount == clicksBefore + 1 && hit != overlayHandle;

        overlayThread.Invoke(overlay.Hide);
        return new Row(monitor.Device, monitor.ScalePercent, target, expected, measured, errors,
            aligned, clickThrough, noFocus, image, elsewhere);
    }

    // Moves in two steps because crossing monitors triggers a DPI change that resizes the window.
    private static void PlaceOnMonitor(IntPtr hwnd, TestNative.MonitorDescription monitor)
    {
        var b = monitor.Bounds;
        TestNative.MoveWindow(hwnd, b.Left + 60, b.Top + 60);
        Thread.Sleep(500);
        TestNative.GetWindowRect(hwnd, out var r);
        TestNative.MoveWindow(hwnd,
            b.Left + (b.Right - b.Left - (r.Right - r.Left)) / 2,
            b.Top + (b.Bottom - b.Top - (r.Bottom - r.Top)) / 2);
        Thread.Sleep(600);
    }

    private static Bitmap Screenshot(TestNative.Rect bounds)
    {
        var bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        var dest = g.GetHdc();
        try
        {
            var screen = GetDC(IntPtr.Zero);
            try
            {
                // CAPTUREBLT is required to include layered (transparent) windows such as the overlay;
                // Graphics.CopyFromScreen rejects that flag, so call BitBlt directly.
                const int SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000;
                if (!BitBlt(dest, 0, 0, bitmap.Width, bitmap.Height, screen, bounds.Left, bounds.Top, SrcCopy | CaptureBlt))
                    throw new InvalidOperationException("BitBlt failed.");
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
        finally
        {
            g.ReleaseHdc(dest);
        }
        return bitmap;
    }

    /// <summary>Bounding box, in screen pixels, of everything close to the ring colour; null if none.</summary>
    private static ScreenRect? FindRing(Bitmap shot, TestNative.Rect monitor)
    {
        var rgb = OverlayGeometry.RingColorRgb;
        int tr = (rgb >> 16) & 0xFF, tg = (rgb >> 8) & 0xFF, tb = rgb & 0xFF;

        var data = shot.LockBits(new Rectangle(0, 0, shot.Width, shot.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            var row = new byte[data.Stride];
            for (var y = 0; y < data.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (var x = 0; x < data.Width; x++)
                {
                    int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    if (Math.Abs(r - tr) > ColorTolerance || Math.Abs(g - tg) > ColorTolerance || Math.Abs(b - tb) > ColorTolerance)
                        continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            return maxX < 0
                ? null
                : new ScreenRect(monitor.Left + minX, monitor.Top + minY, maxX - minX + 1, maxY - minY + 1);
        }
        finally
        {
            shot.UnlockBits(data);
        }
    }

    // Crops around the expected box and draws it, so a miss can be seen at a glance.
    private static void SaveEvidence(Bitmap shot, TestNative.Rect monitor, ScreenRect expected, string path)
    {
        const int margin = 60;
        var local = new Rectangle(expected.X - monitor.Left - margin, expected.Y - monitor.Top - margin,
            expected.Width + 2 * margin, expected.Height + 2 * margin);
        local.Intersect(new Rectangle(0, 0, shot.Width, shot.Height));
        if (local.IsEmpty) local = new Rectangle(0, 0, Math.Min(shot.Width, 400), Math.Min(shot.Height, 300));

        using var crop = shot.Clone(local, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
        using (var pen = new Pen(Color.Lime, 1))
        {
            g.DrawRectangle(pen, expected.X - monitor.Left - local.X, expected.Y - monitor.Top - local.Y,
                expected.Width - 1, expected.Height - 1);
        }
        crop.Save(path, ImageFormat.Png);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

    private static string Sanitize(string device) =>
        new(device.Where(char.IsLetterOrDigit).ToArray());

    private static string Table(IEnumerable<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("monitor      scale  target                    expected outer box         measured                   edge error px (L,T,R,B)  aligned  clickthrough  nofocus");
        foreach (var r in rows)
        {
            string Fmt(ScreenRect x) => $"{x.X},{x.Y} {x.Width}x{x.Height}";
            sb.AppendLine(string.Join("  ",
                r.Monitor.PadRight(11), $"{r.Scale}%".PadLeft(5), Fmt(r.Target).PadRight(24), Fmt(r.Expected).PadRight(24),
                (r.Measured is { } m ? Fmt(m) : r.FoundElsewhere ?? "ring not found").PadRight(24),
                (r.EdgeErrors is { } e ? string.Join(",", e) : "-").PadRight(22),
                (r.Aligned ? "ok" : "FAIL").PadRight(7), (r.ClickThrough ? "ok" : "FAIL").PadRight(12), r.NoFocus ? "ok" : "FAIL"));
        }
        return sb.ToString();
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ashok.Core.Overlay;

namespace Ashok.UI.Overlay;

// OPUS: this is a deliberately naive stand-in; the real overlay is on the plan's "Tricky parts" list
// (Phase 2). It still needs:
//  - Win32 extended styles WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW (and layered), so clicks
//    pass through and the window never takes focus or shows in Alt+Tab. Show() must not activate.
//  - Placement with SetWindowPos in physical pixels. WPF Left/Top/Width/Height are DIPs, so the code
//    below is only correct at 100% scale on a single monitor.
//  - Per-monitor DPI v2 handling when the target is on a different monitor (or a different scale) than
//    the one the window last occupied; WM_DPICHANGED must not resize or move the ring.
//  - Negative coordinates (monitors left of or above the primary) and a ring that straddles monitors.
// Measure with the Desktop-tagged OverlayAlignmentTests (Ashok.Windows.Tests): it screenshots every
// monitor and saves annotated PNGs to %TEMP%\ashok-overlay.
public sealed class OverlayWindow : Window, IOverlay
{
    private readonly Border _ring;
    private bool _pulse = true;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Title = "Ashok overlay";

        var rgb = OverlayGeometry.RingColorRgb;
        var color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        _ring = new Border
        {
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(OverlayGeometry.RingThicknessPx),
        };
        Content = _ring;
    }

    public bool Pulse
    {
        get => _pulse;
        set
        {
            _pulse = value;
            ApplyPulse();
        }
    }

    public void Show(ScreenRect target)
    {
        var box = OverlayGeometry.OuterBox(target);
        Left = box.X;
        Top = box.Y;
        Width = box.Width;
        Height = box.Height;
        ApplyPulse();
        Show();
    }

    private void ApplyPulse()
    {
        if (_pulse)
        {
            _ring.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            _ring.BeginAnimation(OpacityProperty, null);
            _ring.Opacity = 1.0;
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Ashok.Core.Overlay;

namespace Ashok.UI.Overlay;

/// <summary>
/// The ring overlay. A raw Win32 layered window whose pixels are a bitmap drawn in physical pixels and
/// pushed with UpdateLayeredWindow.
///
/// WPF is deliberately not used for the window itself: WPF places and sizes windows in device-independent
/// pixels and rescales them on WM_DPICHANGED, which is why the stub landed at 1.5x size on a 150% screen.
///
/// Guarantees:
/// <list type="bullet">
/// <item>Physical pixels everywhere. The window is created and updated under a per-monitor-v2 DPI context
/// whatever the host process declared, and its window procedure is DefWindowProc, which ignores
/// WM_DPICHANGED. Windows therefore never moves or rescales it, across monitors with different scales,
/// at negative coordinates, or when the ring straddles two monitors.</item>
/// <item>Click-through: WS_EX_LAYERED | WS_EX_TRANSPARENT, so hit-testing skips it entirely.</item>
/// <item>Never takes focus: WS_EX_NOACTIVATE, and it is only ever shown with SWP_NOACTIVATE.</item>
/// <item>Not in Alt+Tab or the taskbar: WS_EX_TOOLWINDOW.</item>
/// </list>
/// Create, use and dispose it on one thread that runs a message loop (the WPF UI thread in the app).
/// </summary>
public sealed class OverlayWindow : IOverlay, IDisposable
{
    public const int CornerRadiusPx = 10;

    private const byte MinPulseAlpha = 90;
    private const double PulsePeriodMs = 1400;
    private const string ClassName = "AshokOverlayRing";

    private const int ExStyles = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;

    private static readonly Lazy<bool> ClassRegistered = new(RegisterWindowClass);

    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _pulseTimer;
    private readonly Stopwatch _clock = new();
    private RingBitmap? _bitmap;
    private ScreenRect _box;
    private bool _visible;
    private bool _pulse = true;
    private bool _disposed;

    public OverlayWindow()
    {
        _ = ClassRegistered.Value;
        using (DpiScope.PerMonitorV2())
        {
            _hwnd = CreateWindowEx(ExStyles, ClassName, "Ashok overlay", WS_POPUP, 0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        }
        if (_hwnd == IntPtr.Zero) throw new Win32Exception();

        _pulseTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _pulseTimer.Tick += (_, _) => Present();
    }

    /// <summary>The native window handle, for tests and diagnostics.</summary>
    public IntPtr Handle => _hwnd;

    public bool Pulse
    {
        get => _pulse;
        set
        {
            _pulse = value;
            UpdateTimer();
            Present();
        }
    }

    public void Show(ScreenRect target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _box = OverlayGeometry.OuterBox(target);
        _bitmap?.Dispose();
        _bitmap = RingBitmap.Create(RingRaster.Render(
            _box.Width, _box.Height, OverlayGeometry.RingThicknessPx, CornerRadiusPx, OverlayGeometry.RingColorRgb),
            _box.Width, _box.Height);

        _visible = true;
        _clock.Restart();
        Present();
        using (DpiScope.PerMonitorV2())
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        UpdateTimer();
    }

    public void Hide()
    {
        if (_disposed) return;
        _visible = false;
        _pulseTimer.Stop();
        ShowWindow(_hwnd, SW_HIDE);
    }

    /// <summary>Same as <see cref="Dispose"/>; kept for callers written against the WPF stub.</summary>
    public void Close() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pulseTimer.Stop();
        DestroyWindow(_hwnd);
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void UpdateTimer()
    {
        if (_visible && _pulse && !_disposed) _pulseTimer.Start();
        else _pulseTimer.Stop();
    }

    private void Present()
    {
        if (_bitmap is null || !_visible || _disposed) return;
        var alpha = _pulse ? PulseAlpha(_clock.Elapsed.TotalMilliseconds) : (byte)255;
        using (DpiScope.PerMonitorV2())
        {
            _bitmap.Present(_hwnd, _box.X, _box.Y, alpha);
        }
    }

    // Starts fully opaque, eases down to MinPulseAlpha and back once per period.
    private static byte PulseAlpha(double elapsedMs)
    {
        var phase = elapsedMs % PulsePeriodMs / PulsePeriodMs;
        var wave = 0.5 + 0.5 * Math.Cos(phase * 2 * Math.PI);
        return (byte)Math.Round(MinPulseAlpha + (255 - MinPulseAlpha) * wave);
    }

    private static bool RegisterWindowClass()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            // DefWindowProc as the window procedure: it ignores WM_DPICHANGED (so nothing ever rescales the
            // window), and a native function pointer means no managed delegate has to be kept alive.
            lpfnWndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastPInvokeError() != ERROR_CLASS_ALREADY_EXISTS)
            throw new Win32Exception();
        return true;
    }

    /// <summary>Switches the calling thread to per-monitor-v2 DPI awareness and restores it on dispose.</summary>
    private readonly struct DpiScope : IDisposable
    {
        private static readonly IntPtr PerMonitorAwareV2 = new(-4);
        private readonly IntPtr _previous;

        private DpiScope(IntPtr previous) => _previous = previous;

        public static DpiScope PerMonitorV2() => new(SetThreadDpiAwarenessContext(PerMonitorAwareV2));

        public void Dispose()
        {
            if (_previous != IntPtr.Zero) SetThreadDpiAwarenessContext(_previous);
        }
    }

    /// <summary>A 32-bit premultiplied DIB selected into a memory DC, ready for UpdateLayeredWindow.</summary>
    private sealed class RingBitmap : IDisposable
    {
        private readonly IntPtr _dc;
        private readonly IntPtr _bitmap;
        private readonly IntPtr _previous;
        private readonly int _width;
        private readonly int _height;

        private RingBitmap(IntPtr dc, IntPtr bitmap, IntPtr previous, int width, int height)
        {
            _dc = dc;
            _bitmap = bitmap;
            _previous = previous;
            _width = width;
            _height = height;
        }

        public static RingBitmap Create(int[] pixels, int width, int height)
        {
            width = Math.Max(width, 1);
            height = Math.Max(height, 1);
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // negative: top-down rows, matching RingRaster
                biPlanes = 1,
                biBitCount = 32,
            };

            var dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) throw new Win32Exception();
            var bitmap = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero)
            {
                DeleteDC(dc);
                throw new Win32Exception();
            }

            if (pixels.Length == width * height) Marshal.Copy(pixels, 0, bits, pixels.Length);
            var previous = SelectObject(dc, bitmap);
            return new RingBitmap(dc, bitmap, previous, width, height);
        }

        public void Present(IntPtr hwnd, int x, int y, byte alpha)
        {
            var destination = new POINT { X = x, Y = y };
            var size = new SIZE { cx = _width, cy = _height };
            var source = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = AC_SRC_ALPHA };
            if (!UpdateLayeredWindow(hwnd, IntPtr.Zero, ref destination, ref size, _dc, ref source, 0, ref blend, ULW_ALPHA))
                throw new Win32Exception();
        }

        public void Dispose()
        {
            SelectObject(_dc, _previous);
            DeleteObject(_bitmap);
            DeleteDC(_dc);
        }
    }

    // ---- Win32 ----------------------------------------------------------------------------------

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_HIDE = 0;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT destination, ref SIZE size, IntPtr hdcSrc, ref POINT source,
        int colorKey, ref BLENDFUNCTION blend, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ashok.Windows.Tests;

internal static class TestNative
{
    public static readonly IntPtr PerMonitorAwareV2 = new(-4);
    public const uint WM_CLOSE = 0x0010;

    /// <summary>Result of the one-time process-wide DPI awareness request made at test startup.</summary>
    public static bool DpiAwarenessRequestSucceeded { get; private set; }

    // xUnit runs inside testhost.exe, so a manifest on this assembly would have no effect.
    // Request PerMonitorV2 once, before any WPF or UIA code loads.
    [ModuleInitializer]
    internal static void RequestPerMonitorV2() =>
        DpiAwarenessRequestSucceeded = SetProcessDpiAwarenessContext(PerMonitorAwareV2);

    public static bool IsPerMonitorV2() =>
        AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), PerMonitorAwareV2);

    public static List<(IntPtr Handle, string Class, string Title)> TopLevelWindows()
    {
        var list = new List<(IntPtr, string, string)>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && !IsCloaked(h))
            {
                var cls = new StringBuilder(256);
                GetClassName(h, cls, cls.Capacity);
                var title = new StringBuilder(512);
                GetWindowText(h, title, title.Capacity);
                list.Add((h, cls.ToString(), title.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static void RestoreIfMinimized(IntPtr hwnd)
    {
        if (!IsIconic(hwnd)) return;
        ShowWindow(hwnd, 9 /* SW_RESTORE */);
        Thread.Sleep(1500);
    }

    public static void Close(IntPtr hwnd) => PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] public static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
}

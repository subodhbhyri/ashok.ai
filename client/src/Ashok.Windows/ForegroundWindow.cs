using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Ashok.Windows;

public sealed record ForegroundWindow(IntPtr Handle, string Process, string Title);

public interface IForegroundWindowProvider
{
    ForegroundWindow? Get();
}

public sealed class Win32ForegroundWindowProvider : IForegroundWindowProvider
{
    public ForegroundWindow? Get()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        return hwnd == IntPtr.Zero ? null : Describe(hwnd);
    }

    /// <summary>Resolves process name and title for any top-level window handle.</summary>
    public static ForegroundWindow Describe(IntPtr hwnd)
    {
        var title = GetTitle(hwnd);
        var pid = GetProcessId(hwnd);

        // UWP apps (Settings, ...) are framed by ApplicationFrameHost; the real app is a child window.
        if (TryGetProcessName(pid) is "ApplicationFrameHost")
        {
            var inner = FindHostedAppProcessId(hwnd, pid);
            if (inner != 0) pid = inner;
        }

        return new ForegroundWindow(hwnd, TryGetProcessName(pid) ?? "unknown", title);
    }

    private static uint GetProcessId(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    private static string? TryGetProcessName(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static uint FindHostedAppProcessId(IntPtr frame, uint framePid)
    {
        uint found = 0;
        NativeMethods.EnumChildWindows(frame, (child, _) =>
        {
            var pid = GetProcessId(child);
            if (pid != framePid) { found = pid; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}

internal static class NativeMethods
{
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);
}

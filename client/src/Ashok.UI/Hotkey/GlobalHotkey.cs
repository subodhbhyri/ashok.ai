using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Ashok.UI.Hotkey;

/// <summary>A system-wide hotkey (Ctrl+Alt+H by default) received through a hidden message-only window.</summary>
public sealed class GlobalHotkey : IDisposable
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModNoRepeat = 0x4000;
    public const uint KeyH = 0x48;
    public const int WmHotkey = 0x0312;

    public const int HotkeyId = 0xA5C1;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _source;
    private bool _registered;

    public GlobalHotkey(uint modifiers = ModControl | ModAlt, uint key = KeyH)
    {
        _source = new HwndSource(new HwndSourceParameters("AshokHotkey") { ParentWindow = HwndMessage });
        _source.AddHook(WndProc);
        _registered = RegisterHotKey(_source.Handle, HotkeyId, modifiers | ModNoRepeat, key);
    }

    /// <summary>False when another program already owns this combination.</summary>
    public bool IsRegistered => _registered;

    /// <summary>Handle of the hidden window that receives WM_HOTKEY.</summary>
    public IntPtr WindowHandle => _source.Handle;

    public event Action? Pressed;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

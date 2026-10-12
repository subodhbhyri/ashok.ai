using System.Runtime.InteropServices;
using Ashok.Core.Session;

namespace Ashok.Windows;

/// <summary>
/// Says "look again" soon after something changes on screen, with a slow poll as the safety net.
///
/// <para>It listens to WinEvents (out-of-context hooks) rather than registering UI Automation event handlers.
/// Windows raises WinEvents for the same changes UIA reports (focus, a window or menu opening, an element
/// appearing or disappearing, a switch changing state, a name changing), including for XAML and Store apps,
/// but they arrive as plain window messages on a thread we own. That removes the UIA handler problems: no
/// callbacks on COM threads, no deadlock when a handler touches UIA, and no per-window registrations to keep
/// in step with the foreground window.</para>
///
/// <para>Noise control:</para>
/// <list type="bullet">
/// <item>Only events from the process of the window in front count (plus foreground and menu events from
/// anywhere), so a clock or a busy browser in the background does not wake us.</item>
/// <item>Caret, cursor and location events are ignored: they fire on every keystroke and mouse move.</item>
/// <item>A burst of events produces one signal, <see cref="Settle"/> after the first, so the capture sees the
/// screen after the change rather than halfway through it.</item>
/// </list>
/// Events can still be missed (some apps raise none), so <see cref="SafetyPoll"/> keeps signalling regardless.
/// </summary>
public sealed class WinEventWatcher(TimeSpan? settle = null, TimeSpan? safetyPoll = null) : IUiWatcher
{
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(80);
    public static readonly TimeSpan SafetyPoll = TimeSpan.FromMilliseconds(1000);

    public IDisposable Start(Action onChange) => new Subscription(onChange, settle ?? Settle, safetyPoll ?? SafetyPoll);

    private sealed class Subscription : IDisposable
    {
        private readonly Action _onChange;
        private readonly TimeSpan _settle;
        private readonly Timer _debounce;
        private readonly Timer _poll;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private readonly NativeMethods.WinEventProc _callback; // kept alive for as long as the hooks exist
        private readonly int _ownProcessId = Environment.ProcessId;

        private uint _threadId;
        private int _foregroundProcessId;
        private int _pending;
        private int _disposed;

        public Subscription(Action onChange, TimeSpan settle, TimeSpan safetyPoll)
        {
            _onChange = onChange;
            _settle = settle;
            _callback = OnWinEvent;
            _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            _poll = new Timer(_ => SafeInvoke(), null, safetyPoll, safetyPoll);
            _foregroundProcessId = ProcessOf(NativeMethods.GetForegroundWindow());

            _thread = new Thread(Pump) { IsBackground = true, Name = "Ashok WinEvent watcher" };
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(5));
        }

        // Out-of-context WinEvents are delivered to the thread that set the hook, through its message loop.
        private void Pump()
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            var hooks = new List<IntPtr>
            {
                Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MENUPOPUPEND), // foreground, menus, menu pop-ups
                Hook(EVENT_SYSTEM_DIALOGSTART, EVENT_SYSTEM_DIALOGEND),
                Hook(EVENT_OBJECT_CREATE, EVENT_OBJECT_REORDER),           // create, destroy, show, hide, reorder
                Hook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_STATECHANGE),        // focus, selection, state (switches, checkboxes)
                Hook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE),
                Hook(EVENT_OBJECT_VALUECHANGE, EVENT_OBJECT_VALUECHANGE),
            };
            _ready.Set();

            try
            {
                while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
            }
            finally
            {
                foreach (var hook in hooks.Where(h => h != IntPtr.Zero)) NativeMethods.UnhookWinEvent(hook);
            }
        }

        private IntPtr Hook(uint min, uint max) =>
            NativeMethods.SetWinEventHook(min, max, IntPtr.Zero, _callback, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        // Runs on the pump thread for every event: keep it to a few cheap checks.
        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (idObject == OBJID_CARET || idObject == OBJID_CURSOR) return;

            switch (eventType)
            {
                case EVENT_SYSTEM_FOREGROUND:
                    var pid = ProcessOf(hwnd);
                    if (pid != _ownProcessId) Volatile.Write(ref _foregroundProcessId, pid);
                    Schedule();
                    return;
                case >= EVENT_SYSTEM_MENUSTART and <= EVENT_SYSTEM_MENUPOPUPEND:
                    Schedule(); // menus often live in another process (Start, flyouts) yet are part of the task
                    return;
            }

            if (hwnd == IntPtr.Zero) return;
            if (ProcessOf(hwnd) != Volatile.Read(ref _foregroundProcessId)) return;
            Schedule();
        }

        // Leading-edge debounce: the first event of a burst arms one timer, later ones ride along.
        private void Schedule()
        {
            if (Interlocked.Exchange(ref _pending, 1) == 1) return;
            try
            {
                _debounce.Change(_settle, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Fire()
        {
            Volatile.Write(ref _pending, 0);
            SafeInvoke();
        }

        private void SafeInvoke()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                _onChange();
            }
            catch (Exception)
            {
                // The runner reports its own errors; a throwing callback must never kill the watcher's timers.
            }
        }

        private static int ProcessOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 0;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            return (int)pid;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _poll.Dispose();
            _debounce.Dispose();
            if (_threadId != 0) NativeMethods.PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
            _ready.Dispose();
        }
    }

    // ---- Win32 ----------------------------------------------------------------------------------

    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint WM_QUIT = 0x0012;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MENUSTART = 0x0004;
    private const uint EVENT_SYSTEM_MENUPOPUPEND = 0x0007;
    private const uint EVENT_SYSTEM_DIALOGSTART = 0x0010;
    private const uint EVENT_SYSTEM_DIALOGEND = 0x0011;
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_REORDER = 0x8004;
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint EVENT_OBJECT_STATECHANGE = 0x800A;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint EVENT_OBJECT_VALUECHANGE = 0x800E;

    private const int OBJID_CARET = -8;
    private const int OBJID_CURSOR = -9;

    private static class NativeMethods
    {
        public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
    }
}

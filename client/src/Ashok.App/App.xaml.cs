using System.Windows;
using System.Windows.Threading;
using Ashok.App.Tray;
using Ashok.Core.Overlay;
using Ashok.Core.Speech;
using Ashok.Speech;
using Ashok.UI;
using Ashok.UI.Hotkey;
using Ashok.UI.Overlay;
using Ashok.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Ashok.App;

/// <summary>Tray, hotkey and DI wiring. The windows themselves live in Ashok.UI.</summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\Ashok.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        var services = new ServiceCollection();
        services.AddSingleton<IForegroundWindowProvider, Win32ForegroundWindowProvider>();
        services.AddSingleton<IOverlay>(_ => new OverlayWindow());
        services.AddSingleton<ISpeaker, WindowsSpeaker>();
        services.AddSingleton<AskPanel>();
        services.AddSingleton<GlobalHotkey>();
        services.AddSingleton<TrayIcon>();
        _services = services.BuildServiceProvider();

        var panel = _services.GetRequiredService<AskPanel>();
        var speaker = _services.GetRequiredService<ISpeaker>();
        var foreground = _services.GetRequiredService<IForegroundWindowProvider>();
        var tray = _services.GetRequiredService<TrayIcon>();
        var hotkey = _services.GetRequiredService<GlobalHotkey>();

        void Ask()
        {
            // Read the foreground window before the panel takes focus: it is the window the user needs help with.
            var previous = foreground.Get()?.Handle ?? IntPtr.Zero;
            panel.ShowForQuestion(previous);
        }

        hotkey.Pressed += Ask;
        tray.AskRequested += Ask;
        tray.DemoRequested += ShowDemoRing;
        tray.QuitRequested += Shutdown;
        panel.QuestionSubmitted += (_, _) =>
        {
            // Phase 3 replaces this with the session engine.
            const string answer = "Guidance is not connected yet.";
            panel.SetAnswer(answer);
            _ = speaker.SpeakAsync(answer);
        };

        if (!hotkey.IsRegistered)
            tray.Notify("Ashok", "Ctrl+Alt+H is already used by another program. Use the tray icon to ask instead.");
    }

    private void ShowDemoRing()
    {
        // Centre of the monitor under the cursor, in physical pixels (this process is PerMonitorV2).
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
        var target = new ScreenRect(screen.X + (screen.Width - 300) / 2, screen.Y + (screen.Height - 200) / 2, 300, 200);

        var overlay = _services!.GetRequiredService<IOverlay>();
        overlay.Show(target);
        _ = _services!.GetRequiredService<ISpeaker>().SpeakAsync("This is where I will point.");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            overlay.Hide();
        };
        timer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose(); // disposes the hotkey (unregisters it), tray icon and speaker
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

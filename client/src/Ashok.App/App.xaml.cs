using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Ashok.App.Tray;
using Ashok.Core.Evaluation;
using Ashok.Core.Capture;
using Ashok.Core.Intent;
using Ashok.Core.Overlay;
using Ashok.Core.Planner;
using Ashok.Core.Playbooks;
using Ashok.Core.Resolution;
using Ashok.Core.Session;
using Ashok.Core.Speech;
using Ashok.Speech;
using Ashok.UI;
using Ashok.UI.Hotkey;
using Ashok.UI.Overlay;
using Ashok.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Ashok.App;

/// <summary>Tray, hotkey and DI wiring. The windows themselves live in Ashok.UI; the logic in Ashok.Core.</summary>
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

        _services = BuildServices();

        var panel = _services.GetRequiredService<AskPanel>();
        var foreground = _services.GetRequiredService<IForegroundWindowProvider>();
        var runner = _services.GetRequiredService<SessionRunner>();
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

        panel.QuestionSubmitted += (question, previous) => _ = runner.AskAsync(question, previous.ToInt64());
        panel.ConfirmRequested += () => runner.Confirm();
        panel.StopRequested += runner.Stop;

        if (!hotkey.IsRegistered)
            tray.Notify("Ashok", "Ctrl+Alt+H is already used by another program. Use the tray icon to ask instead.");

        // Build the intent index now, off the UI thread, so the first question is not slow.
        var matcher = _services.GetRequiredService<IIntentMatcher>();
        _ = Task.Run(() => matcher.Match("warm up"));
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // Windows and speech
        services.AddSingleton<IForegroundWindowProvider, Win32ForegroundWindowProvider>();
        services.AddSingleton<IOverlay>(_ => new OverlayWindow());
        services.AddSingleton<ISpeaker, WindowsSpeaker>();
        services.AddSingleton<AskPanel>();
        services.AddSingleton<GlobalHotkey>();
        services.AddSingleton<TrayIcon>();

        // Guidance
        services.AddSingleton<IUiDispatcher, WpfDispatcher>();
        services.AddSingleton<ISessionView>(sp => new PanelSessionView(sp.GetRequiredService<AskPanel>()));
        services.AddSingleton<IPlaybookStore>(_ => PlaybookStore.LoadDirectory(PlaybookDirectory(), "win11"));
        services.AddSingleton<IIntentMatcher>(sp => CreateMatcher(sp.GetRequiredService<IPlaybookStore>(), sp.GetRequiredService<TrayIcon>()));
        services.AddSingleton<IInstallTokenStore, FileInstallTokenStore>();
        services.AddSingleton<IPlanner>(sp => CreatePlanner(sp.GetRequiredService<IInstallTokenStore>()));
        services.AddSingleton<IOffPlanDetector, HeuristicOffPlanDetector>();
        services.AddSingleton<TargetResolver>();
        services.AddSingleton<DoneEvaluator>();
        services.AddSingleton<IElementSource, FlaUiElementSource>();
        services.AddSingleton<ISnapshotSource, CandidateSource>();
        services.AddSingleton<IFocusProbe, FocusedElementProbe>();
        services.AddSingleton<IUiWatcher>(_ => new WinEventWatcher());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new GuidanceSession(
            sp.GetRequiredService<IIntentMatcher>(), sp.GetRequiredService<IPlaybookStore>(),
            sp.GetRequiredService<IPlanner>(), sp.GetRequiredService<IOffPlanDetector>(),
            sp.GetRequiredService<TargetResolver>(), sp.GetRequiredService<DoneEvaluator>(),
            sp.GetRequiredService<IOverlay>(), sp.GetRequiredService<ISpeaker>(),
            sp.GetRequiredService<ISessionView>(), sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<TimeProvider>(), Process.GetCurrentProcess().ProcessName,
            sp.GetRequiredService<ISnapshotSource>(), new FileDebugLog()));
        services.AddSingleton<SessionRunner>();

        return services.BuildServiceProvider();
    }

    // ASHOK_API_URL points at the Worker. Without it the app still works: playbooks are fully offline.
    private static IPlanner CreatePlanner(IInstallTokenStore tokens)
    {
        if (!Uri.TryCreate(Environment.GetEnvironmentVariable("ASHOK_API_URL"), UriKind.Absolute, out var baseUrl) ||
            baseUrl.Scheme != Uri.UriSchemeHttps && !baseUrl.IsLoopback)
            return new NullPlanner();

        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var http = new HttpClient { BaseAddress = new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        return new HttpPlanner(http, tokens, new HeuristicRanker(), OsInfoProvider.Get, version);
    }

    private static string PlaybookDirectory()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "playbooks");
        if (Directory.Exists(beside)) return beside;

        // Running from a build output inside the repository.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "playbooks");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return candidate;
        }
        throw new DirectoryNotFoundException("The playbooks folder was not found.");
    }

    private static IIntentMatcher CreateMatcher(IPlaybookStore store, TrayIcon tray)
    {
        if (ModelFiles.FindDirectory() is { } directory &&
            File.Exists(Path.Combine(directory, ModelFiles.Default)) && File.Exists(Path.Combine(directory, ModelFiles.Vocab)))
            return new EmbeddingIntentMatcher(ModelFiles.OpenDefault(directory), store.All);

        // Without the model the word-overlap fallback still finds obvious questions, only less well.
        tray.Notify("Ashok", "The language model files are missing, so matching is basic. Run scripts/fetch-model.ps1.");
        return new EmbeddingIntentMatcher(new HashingEmbedder(), store.All, threshold: 0.35, margin: 0.02);
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
        // OnExit runs on the UI thread, which the overlay (a native window plus a DispatcherTimer) requires.
        // Disposing the container disposes the runner (stops the watcher), overlay, hotkey (unregisters it),
        // tray icon, speaker and UI Automation sources.
        _services?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Runs work on the WPF UI thread without waiting for it.</summary>
public sealed class WpfDispatcher : IUiDispatcher
{
    public void Post(Action action) => Application.Current.Dispatcher.BeginInvoke(action);
}

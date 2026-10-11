using Ashok.Core.Evaluation;
using Ashok.Core.Session;
using static Ashok.Core.Tests.Steps;

namespace Ashok.Core.Tests;

internal sealed class ProbeSource : ISnapshotSource
{
    private int _running;
    private readonly ManualResetEventSlim _gate = new(true);

    public int Calls;
    public int MaxConcurrent;
    public int? LastThread;
    public bool LastWasPoolThread;
    public Exception? ThrowNext;
    public CapturedScreen Result { get; set; } = Rig.Screen("SystemSettings", "Settings", 1);

    /// <summary>While blocked, a capture waits inside Capture() until released.</summary>
    public void Block() => _gate.Reset();

    public void Release() => _gate.Set();

    public CapturedScreen? CaptureWindow(long hwnd) => null;

    public CapturedScreen Capture()
    {
        var now = Interlocked.Increment(ref _running);
        InterlockedMax(ref MaxConcurrent, now);
        Interlocked.Increment(ref Calls);
        LastThread = Environment.CurrentManagedThreadId;
        LastWasPoolThread = Thread.CurrentThread.IsThreadPoolThread;
        try
        {
            _gate.Wait(TimeSpan.FromSeconds(10));
            if (ThrowNext is { } e)
            {
                ThrowNext = null;
                throw e;
            }
            return Result;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}

internal sealed class NoFocus : IFocusProbe
{
    public FocusedElement? Get() => null;
}

internal sealed class ManualWatcher : IUiWatcher
{
    private Action? _onChange;
    public int Starts;
    public int Stops;

    public IDisposable Start(Action onChange)
    {
        Interlocked.Increment(ref Starts);
        _onChange = onChange;
        return new Stopper(this);
    }

    public void Fire() => _onChange?.Invoke();

    private sealed class Stopper(ManualWatcher owner) : IDisposable
    {
        public void Dispose() => Interlocked.Increment(ref owner.Stops);
    }
}

public class SessionRunnerTests
{
    private static (Rig Rig, SessionRunner Runner, ProbeSource Source, ManualWatcher Watcher) Build()
    {
        var book = Book("g", Step("s1", Visible("Button", "Never")), Step("s2", Confirm()));
        var rig = new Rig([book], new FakeMatcher(("go", "g")));
        var source = new ProbeSource();
        var watcher = new ManualWatcher();
        var runner = new SessionRunner(rig.Session, source, new NoFocus(), watcher);
        return (rig, runner, source, watcher);
    }

    private static void WaitFor(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)), "Timed out waiting for a condition.");

    [Fact]
    public async Task Signals_that_arrive_during_a_capture_are_skipped_not_queued()
    {
        var (_, runner, source, watcher) = Build();
        source.Block();

        await runner.AskAsync("go", 0);            // entering Guiding triggers the first capture
        WaitFor(() => source.Calls == 1);
        for (var i = 0; i < 20; i++) watcher.Fire();

        Assert.Equal(1, source.Calls);
        Assert.True(runner.SkippedSignals >= 20);
        Assert.Equal(1, source.MaxConcurrent);

        source.Release();
        WaitFor(() => source.Calls == 1 && runner.Captures == 1);
        // nothing queued up behind it: after it finishes, a new signal is accepted and runs exactly once more
        Thread.Sleep(100);
        Assert.Equal(1, source.Calls);
        watcher.Fire();
        WaitFor(() => source.Calls == 2);
    }

    [Fact]
    public async Task Captures_run_on_a_pool_thread_not_the_callers()
    {
        var (_, runner, source, _) = Build();

        await runner.AskAsync("go", 0);
        WaitFor(() => source.Calls >= 1);

        Assert.NotEqual(Environment.CurrentManagedThreadId, source.LastThread);
        Assert.True(source.LastWasPoolThread);
    }

    [Fact]
    public async Task Many_threads_firing_at_once_never_overlap_captures()
    {
        var (_, runner, source, watcher) = Build();
        await runner.AskAsync("go", 0);
        WaitFor(() => source.Calls >= 1);

        var fire = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++) watcher.Fire();
        }));
        await Task.WhenAll(fire);
        Thread.Sleep(200);

        Assert.Equal(1, source.MaxConcurrent);
        Assert.True(runner.SkippedSignals + runner.Captures >= 4000);
    }

    [Fact]
    public async Task The_watcher_runs_only_while_guiding()
    {
        var (rig, runner, source, watcher) = Build();
        Assert.Equal(0, watcher.Starts);

        await runner.AskAsync("go", 0);
        Assert.Equal(1, watcher.Starts);
        Assert.Equal(0, watcher.Stops);

        runner.Stop();
        Assert.Equal(1, watcher.Stops);
        Assert.Equal(SessionPhase.Idle, rig.Session.State.Phase);

        await runner.AskAsync("go", 0);
        Assert.Equal(2, watcher.Starts);
        Assert.True(source.Calls >= 0);
    }

    [Fact]
    public async Task A_failed_capture_is_reported_and_the_next_signal_still_works()
    {
        var (_, runner, source, watcher) = Build();
        Exception? reported = null;
        runner.Error += e => reported = e;
        source.ThrowNext = new InvalidOperationException("window closed");

        await runner.AskAsync("go", 0);
        WaitFor(() => reported is not null);
        var before = source.Calls;

        watcher.Fire();
        WaitFor(() => source.Calls > before);

        Assert.Equal("window closed", reported!.Message);
    }

    [Fact]
    public async Task Confirm_through_the_runner_completes_a_userConfirms_step()
    {
        var book = Book("c", Step("s1", Confirm()));
        var rig = new Rig([book], new FakeMatcher(("c", "c")));
        var source = new ProbeSource();
        var runner = new SessionRunner(rig.Session, source, new NoFocus(), new ManualWatcher());

        await runner.AskAsync("c", 0);
        WaitFor(() => source.Calls >= 1);
        Assert.True(runner.Confirm());

        WaitFor(() => rig.Session.State.Phase == SessionPhase.Done);
    }

    [Fact]
    public async Task A_candidate_the_ranker_would_drop_still_satisfies_a_step_through_the_runner()
    {
        // Step waits for a "Downloads" heading (Text) that repeats the sidebar item's name.
        var book = Book("d", Step("s1", Visible("Text", "Downloads")));
        var rig = new Rig([book], new FakeMatcher(("d", "d")));
        var source = new ProbeSource
        {
            Result = Rig.Screen("explorer", "Downloads", 1, Els.El("TreeItem", "Downloads"), Els.El("Text", "Downloads", x: 400)),
        };
        var runner = new SessionRunner(rig.Session, source, new NoFocus(), new ManualWatcher());

        await runner.AskAsync("d", 0);

        WaitFor(() => rig.Session.State.Phase == SessionPhase.Done);
    }
}

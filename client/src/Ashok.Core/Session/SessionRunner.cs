namespace Ashok.Core.Session;

/// <summary>
/// Drives a <see cref="GuidanceSession"/>: while it is guiding, each watcher signal triggers one capture on the
/// thread pool and feeds the result in. A signal that arrives while a capture is still running is skipped,
/// not queued, so captures never overlap and a slow window cannot build up a backlog.
/// </summary>
public sealed class SessionRunner : IDisposable
{
    private readonly GuidanceSession _session;
    private readonly ISnapshotSource _source;
    private readonly IFocusProbe _focus;
    private readonly IUiWatcher _watcher;
    private readonly object _watchGate = new();
    private IDisposable? _subscription;
    private int _busy;
    private int _captures;
    private int _skipped;

    public SessionRunner(GuidanceSession session, ISnapshotSource source, IFocusProbe focus, IUiWatcher watcher)
    {
        _session = session;
        _source = source;
        _focus = focus;
        _watcher = watcher;
        _session.StateChanged += OnStateChanged;
    }

    public int Captures => Volatile.Read(ref _captures);

    /// <summary>Signals dropped because a capture was already running.</summary>
    public int SkippedSignals => Volatile.Read(ref _skipped);

    /// <summary>Raised when a capture or tick throws (for example the window closed mid-capture).</summary>
    public event Action<Exception>? Error;

    /// <summary>Matching can be slow (an embedding model), so it runs off the caller's thread.</summary>
    public Task AskAsync(string question, long previousWindow) => Task.Run(() => _session.AskAsync(question, previousWindow));

    public bool Confirm()
    {
        var accepted = _session.Confirm();
        if (accepted) Signal();
        return accepted;
    }

    public void Stop() => _session.Cancel();

    /// <summary>Called by the watcher, from any thread.</summary>
    public void Signal()
    {
        if (_session.State.Phase != SessionPhase.Guiding) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Interlocked.Increment(ref _skipped);
            return;
        }
        _ = Task.Run(CaptureAndTickAsync);
    }

    private async Task CaptureAndTickAsync()
    {
        try
        {
            if (_session.State.Phase != SessionPhase.Guiding) return;
            Interlocked.Increment(ref _captures);
            var screen = _source.Capture();
            var focus = _focus.Get();
            await _session.TickAsync(screen, focus);
        }
        catch (Exception e)
        {
            Error?.Invoke(e);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void OnStateChanged(SessionState state)
    {
        lock (_watchGate)
        {
            if (state.Phase == SessionPhase.Guiding)
            {
                _subscription ??= _watcher.Start(Signal);
            }
            else
            {
                _subscription?.Dispose();
                _subscription = null;
            }
        }
        if (state.Phase == SessionPhase.Guiding) Signal(); // look at the screen straight away
    }

    public void Dispose()
    {
        _session.StateChanged -= OnStateChanged;
        lock (_watchGate)
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }
}

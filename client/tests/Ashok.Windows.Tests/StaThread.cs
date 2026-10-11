using System.Windows.Threading;

namespace Ashok.Windows.Tests;

/// <summary>An STA thread running a WPF dispatcher, for creating and driving windows from tests.</summary>
internal sealed class StaThread : IDisposable
{
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;

    public StaThread()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "StaThread" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
    }

    public T Invoke<T>(Func<T> work) => _dispatcher.Invoke(work);

    public void Invoke(Action work) => _dispatcher.Invoke(work);

    public void Dispose()
    {
        _dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}

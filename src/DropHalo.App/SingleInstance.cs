using Windows.Win32;

namespace DropHalo.App;

/// <summary>
/// One DropHalo per session. A second launch hands over to the first and exits: a plain launch asks it
/// to show its task window, <c>--quit</c> asks it to exit. Named auto-reset events, so a signal sent
/// before the first instance starts listening is not lost.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Name = @"Local\DropHalo.App";
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;
    private readonly EventWaitHandle _show = new(false, EventResetMode.AutoReset, Name + ".Show");
    private readonly EventWaitHandle _quit = new(false, EventResetMode.AutoReset, Name + ".Quit");
    private readonly List<RegisteredWaitHandle> _waits = [];

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: false, Name);
        try
        {
            _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        IsFirst = _ownsMutex;
    }

    public bool IsFirst { get; }

    /// <summary>Second instance: pass the request on. The first instance may bring a window forward, so it is allowed to.</summary>
    public void Signal(bool quit)
    {
        PInvoke.AllowSetForegroundWindow(PInvoke.ASFW_ANY);
        (quit ? _quit : _show).Set();
    }

    /// <summary>First instance: <paramref name="show"/> and <paramref name="quit"/> run on a thread-pool thread.</summary>
    public void Listen(Action show, Action quit)
    {
        _waits.Add(ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false));
        _waits.Add(ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) => quit(), null, Timeout.Infinite, executeOnlyOnce: false));
    }

    public void Dispose()
    {
        foreach (var wait in _waits)
        {
            using var drained = new ManualResetEvent(false);
            if (wait.Unregister(drained))
            {
                drained.WaitOne(TimeSpan.FromSeconds(1));
            }
        }

        _waits.Clear();
        _show.Dispose();
        _quit.Dispose();
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
    }
}

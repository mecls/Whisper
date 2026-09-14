using System.Windows.Threading;

namespace Spit.App;

/// One running Spit (rule 36): two instances would install two hooks and paste every dictation twice.
///
/// A named mutex decides who is first. The activation signal is a named auto-reset event that both
/// sides create-or-open *before* the mutex check, so a second launch's `Set()` is never lost in the
/// moment between the first instance taking the mutex and starting to wait: an auto-reset event stays
/// signalled until someone waits on it.
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\co.miraside.voice.spit";
    public const string ActivateEventName = @"Local\co.miraside.voice.spit.activate";

    private readonly Dispatcher dispatcher;
    private readonly string mutexName;
    private readonly string activateEventName;
    private Mutex? mutex;
    private bool owned;
    private EventWaitHandle? activate;
    private RegisteredWaitHandle? registration;
    private bool disposed;

    /// Call on the UI thread: `Activated` is raised on this thread's dispatcher.
    public SingleInstance() : this(MutexName, ActivateEventName)
    {
    }

    /// Tests use their own names, so they never meet a Spit that is really running.
    internal SingleInstance(string mutexName, string activateEventName)
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        this.mutexName = mutexName;
        this.activateEventName = activateEventName;
    }

    /// Raised on the UI thread when a second launch asked this instance to come forward.
    public event Action? Activated;

    /// True when this is the first instance (it then listens for activation). False when another
    /// instance owns the mutex; that instance has been signalled and the caller should exit 0.
    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (owned) return true;

        activate ??= new EventWaitHandle(false, EventResetMode.AutoReset, activateEventName);
        mutex ??= new Mutex(initiallyOwned: false, mutexName);
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing it; the mutex is now ours.
            owned = true;
        }

        if (!owned)
        {
            Log.Info("single-instance", "another instance is running; signalling it");
            // The process the user just launched holds the foreground right; hand it to the first instance
            // so its window can actually come forward rather than flash in the taskbar.
            _ = UiNative.AllowSetForegroundWindow(UiNative.ASFW_ANY);
            activate.Set();
            return false;
        }

        registration = ThreadPool.RegisterWaitForSingleObject(activate, OnSignal, null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        registration?.Unregister(null);
        if (owned && mutex is not null)
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Released from a thread that doesn't own it; the OS releases it when the process exits.
            }
        }
        mutex?.Dispose();
        activate?.Dispose();
    }

    private void OnSignal(object? state, bool timedOut)
    {
        if (timedOut || disposed) return;
        dispatcher.BeginInvoke(() => Activated?.Invoke());
    }
}

using System.Windows.Threading;

namespace Spit.App.Tests;

/// Rule 36: a second launch finds the first, tells it to come forward, and exits.
public sealed class SingleInstanceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [WindowsFact]
    public void ASecondAcquire_ReturnsFalseAndSignalsTheFirst()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutexName = $@"Local\co.miraside.voice.spit.test-{suffix}";
        var eventName = $@"Local\co.miraside.voice.spit.test-{suffix}.activate";
        using var ready = new ManualResetEventSlim();
        using var signalled = new ManualResetEventSlim();
        Dispatcher? firstDispatcher = null;
        var firstAcquired = false;
        Exception? failure = null;

        // The first instance owns the mutex from its own thread, as the app's UI thread does, and pumps a
        // dispatcher so `Activated` can be raised on it. A mutex is re-entrant for the thread that owns it,
        // so the second acquire has to come from a different thread to mean anything.
        var thread = new Thread(() =>
        {
            try
            {
                using var first = new SingleInstance(mutexName, eventName);
                first.Activated += signalled.Set;
                firstAcquired = first.TryAcquire();
                firstDispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            }
            catch (Exception e)
            {
                failure = e;
                ready.Set();
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            Assert.True(ready.Wait(Patience), "the first instance did not start");
            Assert.Null(failure);
            Assert.True(firstAcquired);

            using var second = new SingleInstance(mutexName, eventName);
            Assert.False(second.TryAcquire());
            Assert.True(signalled.Wait(Patience), "the first instance was not signalled");
        }
        finally
        {
            firstDispatcher?.InvokeShutdown();
            thread.Join(Patience);
        }
    }
}

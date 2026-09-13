using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Spit.Core;

namespace Spit.App;

/// <summary>
/// The Windows form of mac/Voice/Hotkey/EventTap.swift: a listen-only `WH_KEYBOARD_LL` hook (rule 29).
///
/// The hook lives on a thread of its own with its own message loop, so a busy UI thread can never make the
/// callback late. Late matters: past `LowLevelHooksTimeout` Windows removes the hook without telling
/// anyone — the Mac's `tapDisabledByTimeout`, minus the notification. That is also why the callback does
/// nothing but copy four fields, enqueue them and hand the key on; the work happens on the dispatcher.
///
/// Every key is passed on with `CallNextHookEx`. Swallowing Right Alt would break @, €, [ and { on
/// Portuguese keyboards.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const string Category = "hotkey";
    private const uint ReinstallMessage = Native.WM_APP + 1;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

    private readonly SynchronizationContext context;
    // Held for the life of this object: native code keeps only the function pointer, and a collected
    // delegate turns the next key press into a crash.
    private readonly Native.LowLevelKeyboardProc proc;
    private readonly nint procPointer;
    private readonly SendOrPostCallback drain;
    private readonly ConcurrentQueue<RawKeyEvent> pending = new();
    private readonly Lock gate = new();

    private Thread? thread;
    private uint threadId;
    private nint hook;                              // hook thread only
    private volatile bool installed;
    private ManualResetEventSlim? reinstallDone;
    private int drainScheduled;
    private int callbackFailures;

    /// <param name="context">Where `KeyEvent` is raised: the WPF dispatcher's context.</param>
    public KeyboardHook(SynchronizationContext context)
    {
        this.context = context;
        proc = Callback;
        procPointer = Marshal.GetFunctionPointerForDelegate(proc);
        drain = Drain;
    }

    /// Every key down and up, in order, on the context given to the constructor.
    public event Action<RawKeyEvent>? KeyEvent;

    public bool IsInstalled => installed;

    /// Starts the hook thread and installs the hook. Returns whether the hook is in place.
    public bool Start()
    {
        lock (gate)
        {
            if (thread is not null) return installed;
            var ready = new ManualResetEventSlim();
            var t = new Thread(() => Run(ready)) { IsBackground = true, Name = "Spit keyboard hook" };
            t.Start();
            if (!ready.Wait(CommandTimeout)) HookLog.Error(Category, "keyboard hook thread did not start in time");
            thread = t;
            return installed;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (thread is null) return;
            if (!Native.PostThreadMessage(threadId, Native.WM_QUIT, 0, 0))
            {
                HookLog.Error(Category, $"could not stop the keyboard hook thread: error {Marshal.GetLastPInvokeError()}");
            }
            else if (!thread.Join(CommandTimeout))
            {
                HookLog.Error(Category, "keyboard hook thread did not stop in time");
            }
            thread = null;
        }
    }

    /// <summary>
    /// Unhooks and hooks again on the hook thread, and returns once that is done. Windows can remove the hook
    /// silently, so this runs on resume, unlock and a timer (see `HookWatchdog`) — and only when no key is
    /// held, because a hook replaced mid-press never sees that key come up.
    /// </summary>
    public bool Reinstall()
    {
        lock (gate)
        {
            if (thread is null) return false;
            // Not disposed: if the wait times out, the hook thread may still set it.
            var done = new ManualResetEventSlim();
            Volatile.Write(ref reinstallDone, done);
            try
            {
                if (!Native.PostThreadMessage(threadId, ReinstallMessage, 0, 0))
                {
                    HookLog.Error(Category, $"could not ask for a hook re-install: error {Marshal.GetLastPInvokeError()}");
                    return false;
                }
                if (!done.Wait(CommandTimeout))
                {
                    HookLog.Error(Category, "keyboard hook re-install did not finish in time");
                    return false;
                }
                return installed;
            }
            finally
            {
                Volatile.Write(ref reinstallDone, null);
            }
        }
    }

    public void Dispose() => Stop();

    private void Run(ManualResetEventSlim ready)
    {
        threadId = Native.GetCurrentThreadId();
        // A thread has no message queue until it asks for one, and PostThreadMessage to a thread without one
        // fails — Stop and Reinstall would then do nothing.
        Native.PeekMessage(out _, 0, 0, 0, Native.PM_NOREMOVE);
        Install();
        ready.Set();

        while (true)
        {
            // The hook callback is called from inside this wait; the loop itself only sees our own messages.
            var result = Native.GetMessage(out var msg, 0, 0, 0);
            if (result == 0) break;
            if (result == -1)
            {
                HookLog.Error(Category, $"keyboard hook message loop failed: error {Marshal.GetLastPInvokeError()}");
                break;
            }
            if (msg.message == ReinstallMessage)
            {
                Uninstall();
                Install();
                Volatile.Read(ref reinstallDone)?.Set();
            }
        }
        Uninstall();
    }

    private void Install()
    {
        hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, procPointer, Native.GetModuleHandle(null), 0);
        installed = hook != 0;
        if (installed) HookLog.Info(Category, "keyboard hook installed");
        else HookLog.Error(Category, $"SetWindowsHookEx failed: error {Marshal.GetLastPInvokeError()}");
    }

    private void Uninstall()
    {
        if (hook == 0) return;
        if (!Native.UnhookWindowsHookEx(hook))
        {
            // Usually means Windows already removed it after a timeout; the handle is dead either way.
            HookLog.Error(Category, $"UnhookWindowsHookEx failed: error {Marshal.GetLastPInvokeError()}");
        }
        hook = 0;
        installed = false;
    }

    private unsafe nint Callback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var data = (Native.KBDLLHOOKSTRUCT*)lParam;
                var message = (uint)wParam;
                pending.Enqueue(new RawKeyEvent((int)data->vkCode, (int)data->scanCode, (int)data->flags,
                    message is Native.WM_KEYUP or Native.WM_SYSKEYUP, data->time));
                // One post per burst, not per key: the dispatcher drains everything queued by then.
                if (Interlocked.Exchange(ref drainScheduled, 1) == 0) context.Post(drain, null);
            }
        }
        catch
        {
            // An exception escaping into user32 ends the process, and the key must reach the application
            // regardless. Counted here, logged from the dispatcher.
            Interlocked.Increment(ref callbackFailures);
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }

    private void Drain(object? _)
    {
        // Cleared before draining, so a key enqueued while this runs schedules another drain rather than waiting.
        Volatile.Write(ref drainScheduled, 0);
        var failures = Interlocked.Exchange(ref callbackFailures, 0);
        if (failures > 0) HookLog.Error(Category, $"keyboard hook callback failed {failures} time(s)");
        while (pending.TryDequeue(out var e)) KeyEvent?.Invoke(e);
    }
}

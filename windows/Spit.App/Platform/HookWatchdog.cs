using Microsoft.Win32;

namespace Spit.App;

/// <summary>
/// When to re-install the keyboard hook (rule 29). Windows removes a low-level hook without notice, so it is
/// put back on resume from sleep, on session unlock and every five minutes — but only while idle (no key
/// held, not recording, not latched). A trigger that arrives mid-gesture is not dropped: it is retried every
/// second until the app is idle.
///
/// Everything runs on the given context, so `isIdle` can read UI state directly.
/// </summary>
public sealed class HookWatchdog : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    private const string Category = "hotkey";

    private readonly Func<bool> reinstall;
    private readonly Func<bool> isIdle;
    private readonly SynchronizationContext context;
    private readonly TimeProvider time;

    private ITimer? periodic;
    private ITimer? retry;
    private string? deferredReason;
    private bool started;
    private bool disposed;

    /// <param name="reinstall">`KeyboardHook.Reinstall`; returns whether the hook is back in place.</param>
    /// <param name="isIdle">True when no key is held, nothing is recording and no session is latched.</param>
    public HookWatchdog(Func<bool> reinstall, Func<bool> isIdle, SynchronizationContext context, TimeProvider? time = null)
    {
        this.reinstall = reinstall;
        this.isIdle = isIdle;
        this.context = context;
        this.time = time ?? TimeProvider.System;
    }

    public void Start()
    {
        if (started || disposed) return;
        started = true;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        periodic = time.CreateTimer(_ => context.Post(_ => Trigger("interval"), null), null, Interval, Interval);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (started)
        {
            // Static events hold their subscribers for the life of the process.
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        periodic?.Dispose();
        retry?.Dispose();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) context.Post(_ => Trigger("resume"), null);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock) context.Post(_ => Trigger("unlock"), null);
    }

    private void Trigger(string reason)
    {
        if (disposed) return;
        if (isIdle())
        {
            ReinstallNow(reason);
            return;
        }
        deferredReason = reason;
        retry ??= time.CreateTimer(_ => context.Post(_ => Retry(), null), null, RetryInterval, RetryInterval);
        HookLog.Info(Category, $"hook re-install deferred ({reason}): a gesture is in progress");
    }

    private void Retry()
    {
        if (disposed || deferredReason is null)
        {
            StopRetrying();
            return;
        }
        if (isIdle()) ReinstallNow(deferredReason + ", deferred");
    }

    private void ReinstallNow(string reason)
    {
        deferredReason = null;
        StopRetrying();
        if (reinstall()) HookLog.Info(Category, $"keyboard hook re-installed ({reason})");
        else HookLog.Error(Category, $"keyboard hook re-install failed ({reason})");
    }

    private void StopRetrying()
    {
        retry?.Dispose();
        retry = null;
    }
}

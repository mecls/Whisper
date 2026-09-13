using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;
using static Spit.App.Tests.ClipboardTestSupport;

namespace Spit.App.Tests;

/// The paste path end to end on a real Windows desktop, the way a dictation drives it: the user's clipboard is
/// captured, the text is placed, Ctrl+V lands in another app, and the user's clipboard comes back after the
/// ceiling (rules 31–33). Also checks what `ForegroundContext` reports for that app. The unit tests cover each
/// piece; only this proves `SendInput` really pastes into a foreground window and the restore is not early.
[Collection(ClipboardCollection.Name)]
public sealed class PasteIntoNotepadTests
{
    private const string Dictated = "spit paste test 42";
    private const string UsersClipboard = "the user's own clipboard";

    [WindowsFact]
    public void ADictationPastesIntoNotepadAndTheUsersClipboardComesBackAfterTheCeiling()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Process? notepad = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                using var owner = new HwndSource(new HwndSourceParameters("SpitPasteTestOwner") { ParentWindow = new nint(-3), WindowStyle = 0 });
                Assert.True(RunPatiently(owner.Handle, "test setup", () => ClipboardSession.Empty() && ClipboardSession.SetData(Native.CF_UNICODETEXT, Utf16(UsersClipboard))));

                var started = DateTime.Now.AddSeconds(-1);
                using var launcher = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
                (notepad, var window) = WaitForNotepadWindow(started);
                BringToFront(window);

                var context = ForegroundContext.Current();
                Assert.NotNull(context);
                Assert.Equal("notepad.exe", context.BundleId);
                Assert.False(string.IsNullOrWhiteSpace(context.Name));

                var injector = new TextInjector(() => owner.Handle);
                var result = Wait(injector.InsertAsync(Dictated, "notepad.exe"));
                Assert.Equal(InsertResult.Pasted, result);

                var text = WaitForText(window, Dictated);
                Assert.Contains(Dictated, text);

                // Still the dictation just after the paste: a restore this early would paste the user's clipboard instead.
                Assert.Equal(Dictated, ReadClipboardText(owner.Handle));
                Wait(injector.WaitForRestoreAsync());
                Assert.Equal(UsersClipboard, ReadClipboardText(owner.Handle));
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                try { notepad?.Kill(); } catch (InvalidOperationException) { }
                notepad?.Dispose();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "the paste test hung");
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    /// Runs the dispatcher until `task` completes, so its continuations come back to this thread — the thread that
    /// owns the clipboard owner window, as in the app.
    private static T Wait<T>(Task<T> task)
    {
        Wait((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static (Process Process, nint Window) WaitForNotepadWindow(DateTime startedAfter)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            foreach (var p in Process.GetProcessesByName("notepad"))
            {
                try
                {
                    if (p.StartTime >= startedAfter && p.MainWindowHandle != 0) return (p, p.MainWindowHandle);
                }
                catch (InvalidOperationException) { }
                p.Dispose();
            }
            Pump(TimeSpan.FromMilliseconds(200));
        }
        throw new InvalidOperationException("Notepad's window did not appear within 20 s");
    }

    private static void BringToFront(nint window)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            ShowWindow(window, 9);   // SW_RESTORE
            // Windows lets a process take the foreground only right after input; a synthetic Alt tap is the usual way.
            keybd_event(0x12, 0, 0, 0);
            keybd_event(0x12, 0, 2, 0);
            SetForegroundWindow(window);
            Pump(TimeSpan.FromMilliseconds(300));
            if (Native.GetForegroundWindow() == window) return;
        }
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
        throw new InvalidOperationException($"could not bring Notepad to the front; the foreground belongs to pid {pid}");
    }

    private static string WaitForText(nint window, string expected)
    {
        var clock = Stopwatch.StartNew();
        var last = "";
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            last = ReadNotepadText(window);
            if (last.Contains(expected, StringComparison.Ordinal)) return last;
            Pump(TimeSpan.FromMilliseconds(200));
        }
        return last;
    }

    private static string ReadNotepadText(nint window)
    {
        var root = AutomationElement.FromHandle(window);
        var editor = root.FindFirst(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
        if (editor is null) return "";
        if (editor.TryGetCurrentPattern(TextPattern.Pattern, out var text)) return ((TextPattern)text).DocumentRange.GetText(-1);
        if (editor.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.Value;
        return "";
    }

    private static string? ReadClipboardText(nint owner)
    {
        byte[]? bytes = null;
        RunPatiently(owner, "test read", () =>
        {
            bytes = ClipboardSession.ReadData(Native.CF_UNICODETEXT, ClipboardSnapshot.MaxBytes);
            return true;
        });
        return bytes is null ? null : TextOf(bytes);
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        using var timer = new Timer(_ => frame.Continue = false, null, duration, Timeout.InfiniteTimeSpan);
        Dispatcher.PushFrame(frame);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);
}

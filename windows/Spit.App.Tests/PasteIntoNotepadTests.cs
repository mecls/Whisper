using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
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

                var diagnostics = new StringBuilder();
                diagnostics.Append($"before the paste: {GuiState(window)}; ");
                var injector = new TextInjector(() => owner.Handle);
                var result = Wait(injector.InsertAsync(Dictated, "notepad.exe"));
                var sincePaste = Stopwatch.StartNew();
                Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var foregroundPid);
                diagnostics.Append($"foreground after the paste: pid {foregroundPid} (Notepad pid {notepad.Id}); ");
                Assert.Equal(InsertResult.Pasted, result);

                var text = WaitForText(window, Dictated, diagnostics);
                Assert.True(text.Contains(Dictated, StringComparison.Ordinal), $"Notepad holds \"{text}\". {diagnostics}");

                // Still the dictation inside the ceiling: a restore this early would paste the user's clipboard instead.
                // Checked only while the ceiling has clearly not passed — after it, the restore is correct — and only
                // once Notepad has pasted, because reading earlier could hold the clipboard as Notepad opens it.
                if (sincePaste.Elapsed < TextInjector.RestoreCeiling - TimeSpan.FromMilliseconds(300))
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
            // Windows lets a process take the foreground only right after input. A bare Alt tap is the usual trick,
            // but it leaves a classic Win32 menu bar in keyboard mode, which then swallows Ctrl+V — the first run of
            // this test pasted nothing that way. Alt is masked with the unassigned vkE8, as `MenuMask` does (rule 35).
            keybd_event(0x12, 0, 0, 0);
            keybd_event(0xE8, 0, 0, 0);
            keybd_event(0xE8, 0, 2, 0);
            keybd_event(0x12, 0, 2, 0);
            SetForegroundWindow(window);
            Pump(TimeSpan.FromMilliseconds(300));
            if (Native.GetForegroundWindow() == window) return;
        }
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
        throw new InvalidOperationException($"could not bring Notepad to the front; the foreground belongs to pid {pid}");
    }

    private static string WaitForText(nint window, string expected, StringBuilder diagnostics)
    {
        var clock = Stopwatch.StartNew();
        var last = "";
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            last = ReadThroughWindowMessages(window);
            if (last.Contains(expected, StringComparison.Ordinal)) return last;
            var automation = ReadThroughAutomation(window, out _);
            if (automation.Contains(expected, StringComparison.Ordinal)) return automation;
            Pump(TimeSpan.FromMilliseconds(200));
        }
        diagnostics.Append($"child windows: [{string.Join(", ", ChildClasses(window))}]; ");
        ReadThroughAutomation(window, out var found);
        diagnostics.Append($"automation: {found}");
        return last;
    }

    /// WM_GETTEXT on Notepad's editor child: works across processes for Edit and RichEdit controls.
    private static string ReadThroughWindowMessages(nint window)
    {
        foreach (var (child, name) in Children(window))
        {
            if (!name.StartsWith("Edit", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) continue;
            var length = (int)SendMessage(child, 0x000E, 0, 0);   // WM_GETTEXTLENGTH
            var buffer = new StringBuilder(length + 1);
            SendMessage(child, 0x000D, buffer.Capacity, buffer);  // WM_GETTEXT
            if (buffer.Length > 0) return buffer.ToString();
        }
        return "";
    }

    /// Menu mode and the focused control of the window's GUI thread: a menu bar in keyboard mode eats Ctrl+V.
    private static string GuiState(nint window)
    {
        var thread = Native.GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info)) return $"GetGUIThreadInfo failed ({Marshal.GetLastWin32Error()})";
        var focus = new StringBuilder(256);
        if (info.Focus != 0) GetClassName(info.Focus, focus, focus.Capacity);
        return $"flags 0x{info.Flags:X} (menu mode {(info.Flags & 0x4) != 0}), focus '{focus}'";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public int Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public int CaretLeft, CaretTop, CaretRight, CaretBottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);

    private static string ReadThroughAutomation(nint window, out string found)
    {
        found = "no editor element";
        var root = AutomationElement.FromHandle(window);
        var editor = root.FindFirst(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
        if (editor is null) return "";
        found = $"{editor.Current.ControlType.ProgrammaticName} '{editor.Current.ClassName}'";
        if (editor.TryGetCurrentPattern(TextPattern.Pattern, out var text)) return ((TextPattern)text).DocumentRange.GetText(-1);
        if (editor.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.Value;
        found += " without a text or value pattern";
        return "";
    }

    private static List<(nint Handle, string ClassName)> Children(nint window)
    {
        var children = new List<(nint, string)>();
        EnumChildWindows(window, (child, _) =>
        {
            var name = new StringBuilder(256);
            GetClassName(child, name, name.Capacity);
            children.Add((child, name.ToString()));
            return true;
        }, 0);
        return children;
    }

    private static IEnumerable<string> ChildClasses(nint window) => Children(window).Select(c => c.ClassName);

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

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint hWnd, int msg, int wParam, StringBuilder lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Spit.Core;

namespace Spit.App;

/// The app the user is typing into, the Windows form of `FrontmostContext`. Identity comes only from
/// the executable: `BundleId` is its file name lowercased (`chrome.exe`) and `Name` its FileDescription
/// (`Google Chrome`), so the server's Apps card merges a Mac's and a PC's Chrome into one bar. Window
/// titles are never read: a title can carry an email subject or a document name (rules 26, 31).
public static class ForegroundContext
{
    public const string FrameHostExecutable = "applicationframehost.exe";
    public const string WindowsAppName = "Windows app";

    private const int MaxPath = 32_768;

    public static FrontmostApp? Current()
    {
        var window = Native.GetForegroundWindow();
        if (window == 0) return null;
        if (ProcessIdOf(window) is not { } processId) return null;

        var app = ForProcess(processId);
        if (app is not { BundleId: FrameHostExecutable }) return app;

        // UWP apps are hosted by ApplicationFrameHost.exe; the app itself owns a child window.
        return HostedApp(window, processId) ?? app with { Name = WindowsAppName };
    }

    /// The foreground window's process, for the elevation check at paste time.
    public static int? ForegroundProcessId()
    {
        var window = Native.GetForegroundWindow();
        return window == 0 ? null : ProcessIdOf(window);
    }

    /// Null when the process can't be opened or its image name can't be read (logged).
    public static FrontmostApp? ForProcess(int processId)
    {
        var path = ExecutablePath(processId);
        return path is null ? null : new FrontmostApp(Path.GetFileName(path).ToLowerInvariant(), Describe(path));
    }

    private static string Describe(string path)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            Log.Failure("context", "read FileDescription", e);
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static unsafe string? ExecutablePath(int processId)
    {
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process == 0)
        {
            Log.Win32Failure("context", "OpenProcess", Marshal.GetLastPInvokeError());
            return null;
        }
        try
        {
            var buffer = new char[MaxPath];
            fixed (char* chars = buffer)
            {
                var length = (uint)MaxPath;
                if (!Native.QueryFullProcessImageName(process, 0, chars, ref length))
                {
                    Log.Win32Failure("context", "QueryFullProcessImageName", Marshal.GetLastPInvokeError());
                    return null;
                }
                return new string(chars, 0, (int)length);
            }
        }
        finally
        {
            if (!Native.CloseHandle(process)) Log.Win32Failure("context", "CloseHandle", Marshal.GetLastPInvokeError());
        }
    }

    private static unsafe FrontmostApp? HostedApp(nint frame, int hostProcessId)
    {
        // [0] the host's pid, [1] the first child pid that differs, filled in by the callback.
        var search = new uint[] { (uint)hostProcessId, 0 };
        var handle = GCHandle.Alloc(search);
        try
        {
            Native.EnumChildWindows(frame, &FindHostedChild, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return search[1] == 0 ? null : ForProcess((int)search[1]);
    }

    [UnmanagedCallersOnly]
    private static int FindHostedChild(nint window, nint state)
    {
        var search = (uint[])GCHandle.FromIntPtr(state).Target!;
        if (ProcessIdOf(window) is { } processId && (uint)processId != search[0])
        {
            search[1] = (uint)processId;
            return 0;
        }
        return 1;
    }

    private static int? ProcessIdOf(nint window)
    {
        _ = Native.GetWindowThreadProcessId(window, out var processId);
        if (processId != 0) return (int)processId;
        Log.Win32Failure("context", "GetWindowThreadProcessId", Marshal.GetLastPInvokeError());
        return null;
    }
}

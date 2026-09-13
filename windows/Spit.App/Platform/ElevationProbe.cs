using System.Runtime.InteropServices;

namespace Spit.App;

/// Whether a process runs elevated, for the paste route (rule 30).
public static class ElevationProbe
{
    /// Access denied at any step counts as elevated. A non-elevated Spit is refused an elevated
    /// process's token, and reading that as "not elevated" would send a keystroke Windows drops
    /// silently, then restore the clipboard over the dictation. Any other failure (the process has
    /// exited, say) is logged and counts as not elevated.
    public static bool IsElevated(int processId)
    {
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process == 0) return FailedAt("OpenProcess", Marshal.GetLastPInvokeError());
        try
        {
            return TokenIsElevated(process);
        }
        finally
        {
            Close(process);
        }
    }

    public static bool IsCurrentProcessElevated() => CurrentProcessElevated.Value;

    private static readonly Lazy<bool> CurrentProcessElevated = new(() => TokenIsElevated(Native.GetCurrentProcess()));

    /// Whether Windows will drop Spit's keystrokes into this process: UIPI blocks input only into a process
    /// running *above* the sender, so an elevated target matters only while Spit itself is not elevated. With UAC
    /// off, or Spit run as administrator, every process is elevated alike and the paste works — counting every
    /// window as an admin window there turned every dictation into clipboard-only (found by the Notepad paste
    /// test on a GitHub runner, which runs with UAC off).
    public static bool BlocksInputFromSpit(int processId) => !IsCurrentProcessElevated() && IsElevated(processId);

    private static unsafe bool TokenIsElevated(nint process)
    {
        if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out var token))
            return FailedAt("OpenProcessToken", Marshal.GetLastPInvokeError());
        try
        {
            var elevation = default(Native.TOKEN_ELEVATION);
            if (!Native.GetTokenInformation(token, Native.TokenElevation, &elevation, (uint)sizeof(Native.TOKEN_ELEVATION), out _))
                return FailedAt("GetTokenInformation", Marshal.GetLastPInvokeError());
            return elevation.TokenIsElevated != 0;
        }
        finally
        {
            Close(token);
        }
    }

    private static bool FailedAt(string call, int error)
    {
        Log.Win32Failure("elevation", call, error);
        return error == Native.ERROR_ACCESS_DENIED;
    }

    private static void Close(nint handle)
    {
        if (!Native.CloseHandle(handle)) Log.Win32Failure("elevation", "CloseHandle", Marshal.GetLastPInvokeError());
    }
}

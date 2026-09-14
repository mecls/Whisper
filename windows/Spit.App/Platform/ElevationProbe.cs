using System.Runtime.InteropServices;

namespace Spit.App;

/// Whether a process runs elevated, and whether Windows would drop Spit's input into it, for the paste route (rule 30).
public static unsafe class ElevationProbe
{
    private const int TokenIntegrityLevel = 25;

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

    private static readonly Lazy<int?> CurrentIntegrity = new(() => TokenIntegrity(Native.GetCurrentProcess(), out _));

    /// Whether Windows will drop Spit's keystrokes into this process. UIPI blocks input only into a process at a
    /// higher integrity level than the sender. Checking elevation alone got both directions wrong: with UAC off
    /// every process is elevated and pastes work (every dictation went clipboard-only — found by the Notepad paste
    /// test on a GitHub runner), while an elevated (High) Spit still cannot paste into a SYSTEM-level window (fourth
    /// review). Access denied counts as blocked, as in `IsElevated`.
    public static bool BlocksInputFromSpit(int processId)
    {
        if (CurrentIntegrity.Value is not { } own) return !IsCurrentProcessElevated() && IsElevated(processId);
        return IntegrityOf(processId, out var accessDenied) is { } target ? target > own : accessDenied;
    }

    /// The mandatory integrity level's RID (Low 0x1000, Medium 0x2000, High 0x3000, System 0x4000), or null when it
    /// cannot be read.
    internal static int? IntegrityOf(int processId, out bool accessDenied)
    {
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process == 0)
        {
            accessDenied = FailedAt("OpenProcess", Marshal.GetLastPInvokeError());
            return null;
        }
        try
        {
            return TokenIntegrity(process, out accessDenied);
        }
        finally
        {
            Close(process);
        }
    }

    private static int? TokenIntegrity(nint process, out bool accessDenied)
    {
        accessDenied = false;
        if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out var token))
        {
            accessDenied = FailedAt("OpenProcessToken", Marshal.GetLastPInvokeError());
            return null;
        }
        try
        {
            Native.GetTokenInformation(token, TokenIntegrityLevel, null, 0, out var needed);
            if (needed == 0 || needed > 256)
            {
                FailedAt("GetTokenInformation(size)", Marshal.GetLastPInvokeError());
                return null;
            }
            var buffer = stackalloc byte[(int)needed];
            if (!Native.GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
            {
                accessDenied = FailedAt("GetTokenInformation", Marshal.GetLastPInvokeError());
                return null;
            }
            // TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES, whose first field is the SID pointer.
            var sid = *(nint*)buffer;
            var count = *GetSidSubAuthorityCount(sid);
            if (count == 0) return null;
            return (int)*GetSidSubAuthority(sid, (uint)(count - 1));
        }
        finally
        {
            Close(token);
        }
    }

    private static bool TokenIsElevated(nint process)
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

    [DllImport("advapi32.dll")]
    private static extern byte* GetSidSubAuthorityCount(nint pSid);

    [DllImport("advapi32.dll")]
    private static extern uint* GetSidSubAuthority(nint pSid, uint nSubAuthority);
}

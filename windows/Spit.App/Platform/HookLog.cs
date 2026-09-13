using System.Diagnostics;

namespace Spit.App;

/// Stand-in logger for the hook, capture and streaming layers until the app's rolling log exists; integration
/// points these calls at it. Messages carry error types, codes, lengths and timings — never key identities,
/// audio or transcript text (rule 25).
internal static class HookLog
{
    public static void Info(string category, string message) =>
        Trace.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}");

    public static void Error(string category, string message) =>
        Trace.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] error: {message}");

    /// An exception as a log may carry it: its type and HRESULT, not its message, which can quote a path or a device name.
    public static string Describe(Exception e) => $"{e.GetType().Name} 0x{e.HResult:X8}";
}

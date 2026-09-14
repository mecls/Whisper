using System.IO;

namespace Spit.App;

/// The hook, capture and streaming layers' logging calls, forwarded to the app's rolling `Log`. Messages carry
/// error types, codes, lengths and timings — never key identities, audio or transcript text (rule 25).
internal static class HookLog
{
    public static void Info(string category, string message) => Log.Info(category, message);

    public static void Error(string category, string message) => Log.Error(category, message);

    /// An exception as a log may carry it: its type and HRESULT, not its message, which can quote a path or a device name.
    public static string Describe(Exception e) => $"{e.GetType().Name} 0x{e.HResult:X8}";
}

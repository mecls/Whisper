using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Spit.App;

/// A daily log in `%LOCALAPPDATA%\Miraside\Spit\logs\spit-yyyyMMdd.log` (build spec §14). Lengths,
/// timings, error types and Win32 codes only: never dictated text, clipboard content or window titles
/// (rules 25, 26). Callers pass messages built from those, and nothing else.
public static class Log
{
    private static readonly Lock Gate = new();
    private static AppPaths? paths;

    /// Where the log is written; defaults to the real data folder.
    public static AppPaths Paths
    {
        get { lock (Gate) return paths ?? AppPaths.Default; }
        set { lock (Gate) paths = value; }
    }

    public static void Info(string area, string message) => Write("info", area, message);

    public static void Error(string area, string message) => Write("error", area, message);

    public static void Win32Failure(string area, string call, int errorCode) =>
        Write("error", area, string.Create(CultureInfo.InvariantCulture, $"{call} failed: Win32 error {errorCode}"));

    /// The exception's type and HRESULT, not its message: a message can quote a path or a value.
    public static void Failure(string area, string what, Exception exception) =>
        Write("error", area, string.Create(CultureInfo.InvariantCulture, $"{what} failed: {exception.GetType().Name} (0x{exception.HResult:X8})"));

    /// An exception nobody caught: its type, HRESULT and stack for each exception in the chain — code
    /// locations only, never the messages, which can quote whatever the failing call was handed.
    public static void Crash(string area, string what, Exception exception)
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{what}:"));
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            text.Append(CultureInfo.InvariantCulture, $"{Environment.NewLine}  {e.GetType().FullName} (0x{e.HResult:X8}){Environment.NewLine}{e.StackTrace}");
        }
        Write("fatal", area, text.ToString());
    }

    private static void Write(string level, string area, string message)
    {
        var now = DateTimeOffset.Now;
        var line = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-ddTHH:mm:ss.fffzzz} {level} [{area}] {message}{Environment.NewLine}");
        lock (Gate)
        {
            try
            {
                var directory = (paths ?? AppPaths.Default).Logs;
                var file = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"spit-{now:yyyyMMdd}.log"));
                File.AppendAllText(file, line, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The log is the place failures go, so there is nowhere left but the debugger.
                Debug.WriteLine($"Spit log write failed: {e.GetType().Name}");
            }
        }
    }
}

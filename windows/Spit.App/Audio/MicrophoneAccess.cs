using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace Spit.App;

/// Capture was refused with `E_ACCESSDENIED`: microphone access is off in Windows privacy settings (rule 39).
public sealed class MicrophoneBlockedException : Exception
{
    public MicrophoneBlockedException() : base("Microphone access is blocked.") { }
    public MicrophoneBlockedException(string message) : base(message) { }
    public MicrophoneBlockedException(string message, Exception inner) : base(message, inner) { }
    public MicrophoneBlockedException(Exception inner) : base("Microphone access is blocked.", inner) { }
}

/// No capture device, or any capture failure other than access denied (rule 39).
public sealed class NoMicrophoneException : Exception
{
    public NoMicrophoneException() : base("No microphone is available.") { }
    public NoMicrophoneException(string message) : base(message) { }
    public NoMicrophoneException(string message, Exception inner) : base(message, inner) { }
    public NoMicrophoneException(Exception inner) : base("No microphone is available.", inner) { }
}

/// What the Set-up window's Microphone row shows, and the button next to it (rules 39, 40).
public static class MicrophoneAccess
{
    public const string PrivacySettingsUri = "ms-settings:privacy-microphone";

    private const int EAccessDenied = unchecked((int)0x80070005);

    /// <summary>
    /// Tries a short shared-mode initialise on the default capture device — the call Windows refuses with
    /// `E_ACCESSDENIED` when microphone privacy is off. Nothing is started, so no audio is captured.
    /// </summary>
    public static MicrophoneState Check()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out var device)) return MicrophoneState.NoDevice;
            using (device)
            using (var client = device.CreateAudioClient())
            {
                client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 100 * 10_000L, 0, client.MixFormat, Guid.Empty);
            }
            return MicrophoneState.Available;
        }
        catch (Exception e) when (IsAccessDenied(e))
        {
            HookLog.Info("audio", $"microphone check: blocked ({HookLog.Describe(e)})");
            return MicrophoneState.Blocked;
        }
        catch (Exception e)
        {
            HookLog.Error("audio", $"microphone check failed: {HookLog.Describe(e)}");
            return MicrophoneState.NoDevice;
        }
    }

    public static void OpenSettings()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(PrivacySettingsUri) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            HookLog.Error("audio", $"could not open microphone settings: {HookLog.Describe(e)}");
        }
    }

    /// `E_ACCESSDENIED` anywhere in the chain. NAudio surfaces it as a `CoreAudioException` (a COMException);
    /// the runtime maps the same HRESULT to `UnauthorizedAccessException`. Both carry it in `HResult`.
    internal static bool IsAccessDenied(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is MicrophoneBlockedException || e.HResult == EAccessDenied) return true;
        }
        return false;
    }

    /// The two failures rule 39 distinguishes, so the bar can say what to do.
    internal static Exception Classify(Exception e) => e switch
    {
        MicrophoneBlockedException or NoMicrophoneException => e,
        _ when IsAccessDenied(e) => new MicrophoneBlockedException(e),
        _ => new NoMicrophoneException(e),
    };
}

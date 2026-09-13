namespace Spit.App;

/// What the Set-up window's Microphone row shows (rule 40), and what `MicrophoneAccess.Check` returns —
/// which never returns `Unknown`.
public enum MicrophoneState
{
    /// Not checked yet: the row stays grey with its instruction.
    Unknown,

    /// Capture can open the default input device.
    Available,

    /// Capture failed with `E_ACCESSDENIED`: privacy settings block it (rule 39).
    Blocked,

    /// Any other start failure: no input device (rule 39).
    NoDevice,
}

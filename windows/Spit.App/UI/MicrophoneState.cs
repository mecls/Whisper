namespace Spit.App;

/// What the Set-up window's Microphone row shows (rule 40). Defined here for the UI; if
/// `Audio/MicrophoneAccess.cs` brings its own equivalent, the Coordinator maps it onto this at
/// integration.
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

namespace Spit.Core;

/// The slice of the PC's local settings that refine and sync read or write — the Windows stand-in for
/// the Mac's `Preferences` globals. The app implements it over `settings.json`; tests use an
/// in-memory one. Implementations must tolerate being called from the thread pool (the sync timer).
public interface ILocalSettings
{
    /// `clean` | `literal`. Mirrored from the server on every sync; the server wins.
    string Mode { get; set; }

    /// `auto` | `pt` | `en`. Mirrored from the server on every sync; sent as `languageSetting`.
    string Language { get; set; }

    /// The PC's own dictation key (`rightCtrl` | `rightAlt`). Local only: `SyncService` never writes
    /// it and never sends it, because the server's `hotkey` is the Mac's (rule 46).
    string Hotkey { get; set; }
}

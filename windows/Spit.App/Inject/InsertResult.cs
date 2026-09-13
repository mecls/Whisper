namespace Spit.App;

/// How `TextInjector.InsertAsync` ended; the coordinator maps it to the report's `injected` and the bar's
/// message.
public enum InsertResult
{
    /// Ctrl+V was sent. Report `cleaned` or `raw`.
    Pasted,

    /// Spit itself was the target; the text is on the clipboard. Report `clipboard`, show `Strings.SecureField`.
    ClipboardOnlySelf,

    /// An elevated window was the target; the text is on the clipboard. Report `clipboard`, show `Strings.AdminWindow`.
    ClipboardOnlyElevated,

    /// `SendInput` refused the keystroke. The text stays on the clipboard and nothing is restored over it,
    /// so the user can still paste it. Report `clipboard`.
    ClipboardOnlyKeystrokeFailed,

    /// The clipboard stayed busy past 200 ms or couldn't be written: nothing was pasted or copied. Report
    /// `none`, show `Strings.ClipboardBusy` (rule 33).
    Failed,
}

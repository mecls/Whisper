namespace Spit.App;

/// Where a dictation goes: pasted, or left on the clipboard for one of rule 34's two Windows reasons.
public enum PasteRoute
{
    Paste,

    /// The target is Spit's own window, which would swallow the paste (Mac `mustUseClipboard`).
    ClipboardOnlySelf,

    /// The target is elevated, and `SendInput` into it fails without any error to detect (rule 30).
    ClipboardOnlyElevated,
}

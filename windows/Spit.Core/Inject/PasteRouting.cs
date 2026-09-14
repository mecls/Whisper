namespace Spit.Core;

/// Port of `TextInjector.mustUseClipboard(targetBundleId:secureInputActive:)` and `ownBundleId` in
/// mac/Voice/Inject/TextInjector.swift, with Windows' reasons (prd-spit-mac-windows.md rules 30, 34).
public static class PasteRouting
{
    /// Spit's own `FrontmostApp.BundleId`: the executable file name, lowercased, as the foreground
    /// context reports it (rule 31). One literal, used by both the capture side and this check.
    public const string OwnBundleId = "spit.exe";

    /// Whether this dictation must go to the clipboard instead of being pasted.
    ///
    /// Two reasons, different in kind. An elevated target cannot receive `SendInput` at all, and the
    /// failure is silent — a failed paste followed by a clipboard restore loses the dictation (rule 30).
    /// Spit's own window can receive a paste, which is the problem: dictating while Insights is in front
    /// would type the transcript into Spit instead of wherever the user meant it to go.
    ///
    /// Password fields are not a reason on Windows: a synthetic Ctrl+V works there, and the only
    /// detector is unreliable in Chromium and Electron (rule 34).
    ///
    /// An unknown target (null) pastes as it always has; reading "I don't know which app" as "it might
    /// be me" would divert ordinary dictations to the clipboard for no reason.
    public static bool MustUseClipboard(string? targetBundleId, bool targetIsElevated)
    {
        if (targetIsElevated) return true;
        if (targetBundleId is null) return false;
        return string.Equals(targetBundleId, OwnBundleId, StringComparison.OrdinalIgnoreCase);
    }
}

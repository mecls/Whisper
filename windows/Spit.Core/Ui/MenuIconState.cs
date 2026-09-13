namespace Spit.Core;

/// The tray icon's four looks (prd-spit-mac-windows.md rule 37), one per SF Symbol the Mac's `menuIcon`
/// returns: `mic`, `mic.fill`, `record.circle.fill`, `mic.badge.xmark`.
public enum MenuIconState
{
    Mic,
    MicFilled,
    Recording,
    MicCrossed,
}

/// Port of `VoiceApp.menuIcon`. C# enums carry no methods, so the ranking lives here.
public static class MenuIcon
{
    /// Unauthorized outranks everything; latched outranks listening, because a hands-free session left
    /// running is the one state worth spotting from the tray while the bar sits behind a full-screen app.
    public static MenuIconState For(bool unauthorized, bool latched, HUDState hud)
    {
        if (unauthorized) return MenuIconState.MicCrossed;
        if (latched) return MenuIconState.Recording;
        return hud is HUDState.Listening ? MenuIconState.MicFilled : MenuIconState.Mic;
    }
}

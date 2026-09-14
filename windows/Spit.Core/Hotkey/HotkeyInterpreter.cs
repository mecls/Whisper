namespace Spit.Core;

// Port of mac/Voice/Hotkey/HotkeyInterpreter.swift. Keys are adapted to Windows (rule 27): the Mac's
// `flags(fn:rightOption:rightCommand:)` becomes the state of the two Windows hotkeys, and key codes
// are Windows virtual-key codes. `HotkeyTranslator` produces these from raw hook events.

public abstract record KeyEvent
{
    private KeyEvent() { }
    /// Modifier state of the two right-hand hotkeys, like the Mac's `flagsChanged`.
    public sealed record Flags(bool RightCtrl, bool RightAlt) : KeyEvent;
    /// A non-modifier key went down. `VkCode` is a Windows virtual-key code.
    public sealed record KeyDown(int VkCode) : KeyEvent;
}

public enum HotkeyAction { Press, Release, Cancel }

/// Turns modifier/key events into press/release/cancel. Pure so the rules are testable.
public sealed class HotkeyInterpreter
{
    /// VK_ESCAPE; the Mac's `escapeKeyCode` is 53, its kVK_Escape.
    public const int EscapeKeyCode = 0x1B;

    public HotkeyInterpreter(HotkeyChoice choice) => Choice = choice;

    public HotkeyChoice Choice { get; }

    /// Exposed so the monitor can tell whether a hotkey change while this key is held must be deferred
    /// instead of orphaning the in-flight press.
    public bool IsHeld { get; private set; }

    /// True for the duration of a hands-free session (prd-hands-free-dictation.md rule 6).
    ///
    /// During a hold, any other key can only be a shortcut, so cancelling is right. Latched mode is the
    /// opposite: the user keeps typing while it records, so the session must survive every keystroke.
    /// Esc is the single exception, because "throw this away" has no other expression once the key is
    /// no longer held.
    public bool Latched { get; set; }

    public HotkeyAction? Handle(KeyEvent e)
    {
        switch (e)
        {
            case KeyEvent.Flags f:
                var down = Choice == HotkeyChoice.RightAlt ? f.RightAlt : f.RightCtrl;
                if (down && !IsHeld) { IsHeld = true; return HotkeyAction.Press; }
                if (!down && IsHeld) { IsHeld = false; return HotkeyAction.Release; }
                return null;

            case KeyEvent.KeyDown k:
                // A latched session ignores the keyboard except for Esc. The key is not held during a
                // latched session, so the `IsHeld` guard below would already swallow every key-down —
                // this branch exists to let Esc back through.
                if (Latched) return k.VkCode == EscapeKeyCode ? HotkeyAction.Cancel : null;
                // Any real key while the hotkey is held means a shortcut: not a dictation. `IsHeld`
                // resets here, not at the physical release — `Cancel` is the last action this press
                // produces, and the Flags(down: false) that follows falls through to `return null`.
                if (!IsHeld) return null;
                IsHeld = false;
                return HotkeyAction.Cancel;

            default:
                return null;
        }
    }
}

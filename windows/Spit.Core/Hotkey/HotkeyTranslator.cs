namespace Spit.Core;

/// What a WH_KEYBOARD_LL hook reports for one key event: `KBDLLHOOKSTRUCT.vkCode`, `.scanCode` and
/// `.flags`, whether the message was WM_KEYUP/WM_SYSKEYUP, and `.time` — the GetTickCount millisecond the
/// key really moved (0 when unknown), which `KeyEventClock` turns into gesture time.
public readonly record struct RawKeyEvent(int VkCode, int ScanCode, int Flags, bool IsKeyUp, uint Time = 0);

/// Turns raw hook events into the `KeyEvent`s `HotkeyInterpreter` understands, for one chosen hotkey
/// (prd-spit-mac-windows.md rules 27, 28). Windows-only in meaning, but pure: no Win32 here, so the
/// rules run in `Spit.Core.Tests` on any OS.
///
/// The Mac's event tap gives the interpreter two things the Windows hook does not: modifier presses
/// arrive as `flagsChanged` (never as key-downs, and never repeated), and synthetic events are easy to
/// ignore. This type restores both, so the interpreter's rules apply unchanged.
public sealed class HotkeyTranslator
{
    public const int LlkhfExtended = 0x01;
    public const int LlkhfInjected = 0x10;
    public const int LlkhfUp = 0x80;

    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;
    public const int VkRControl = 0xA3;
    public const int VkRMenu = 0xA5;
    public const int VkEscape = HotkeyInterpreter.EscapeKeyCode;

    /// The Left Ctrl that AltGr layouts (pt-PT among them) send ahead of Right Alt; AutoHotkey's
    /// `SC_FAKE_LCTRL`.
    public const int ScanCodeFakeLeftCtrl = 0x21D;

    public HotkeyTranslator(HotkeyChoice choice) => Choice = choice;

    public HotkeyChoice Choice { get; }

    /// Whether the chosen hotkey is physically down, as far as the events seen so far say.
    public bool IsHotkeyDown { get; private set; }

    /// The right-hand virtual key the choice means, for asking Windows whether it is still down.
    public int VirtualKey => Choice == HotkeyChoice.RightAlt ? VkRMenu : VkRControl;

    /// The hotkey's key-up never arrived but the key is no longer down: Ctrl+Alt+Del, locking the PC or a UAC
    /// prompt moves input to another desktop and the key-up goes with it. Returns the release the interpreter
    /// is owed, or null when nothing was held; either way the next real key-down is a new press, not autorepeat.
    public KeyEvent? ForceRelease()
    {
        if (!IsHotkeyDown) return null;
        IsHotkeyDown = false;
        return new KeyEvent.Flags(RightCtrl: false, RightAlt: false);
    }

    public KeyEvent? Translate(RawKeyEvent e)
    {
        // Rule 28: none of these is "another key". Injected covers Spit's own Ctrl+V and the vkE8
        // menu mask (rule 35).
        if ((e.Flags & LlkhfInjected) != 0) return null;
        if (e.ScanCode == ScanCodeFakeLeftCtrl) return null;

        if (IsHotkey(e))
        {
            if (e.IsKeyUp)
            {
                if (!IsHotkeyDown) return null;
                IsHotkeyDown = false;
                return new KeyEvent.Flags(RightCtrl: false, RightAlt: false);
            }
            // A second key-down while already down is autorepeat (rule 28).
            if (IsHotkeyDown) return null;
            IsHotkeyDown = true;
            return new KeyEvent.Flags(RightCtrl: Choice == HotkeyChoice.RightCtrl, RightAlt: Choice == HotkeyChoice.RightAlt);
        }

        // The Mac only ever sees key-downs, and modifier keys reach it as `flagsChanged` that leave the
        // hotkey's state unchanged — so neither a key-up nor a modifier is "another key".
        if (e.IsKeyUp || IsModifier(e.VkCode)) return null;
        return new KeyEvent.KeyDown(e.VkCode);
    }

    private bool IsHotkey(RawKeyEvent e)
    {
        var extended = (e.Flags & LlkhfExtended) != 0;
        return Choice switch
        {
            HotkeyChoice.RightAlt => e.VkCode == VkRMenu || (e.VkCode == VkMenu && extended),
            _ => e.VkCode == VkRControl || (e.VkCode == VkControl && extended),
        };
    }

    /// The keys macOS reports as `flagsChanged`: Shift, Ctrl, Alt (generic and left/right), the
    /// Windows keys and Caps Lock.
    private static bool IsModifier(int vk) => vk is
        0x10 or 0x11 or 0x12    // VK_SHIFT, VK_CONTROL, VK_MENU
        or 0x14                 // VK_CAPITAL
        or 0x5B or 0x5C         // VK_LWIN, VK_RWIN
        or >= 0xA0 and <= 0xA5; // VK_LSHIFT .. VK_RMENU
}

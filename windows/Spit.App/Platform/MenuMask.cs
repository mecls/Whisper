using System.Runtime.InteropServices;
using Spit.Core;

namespace Spit.App;

/// <summary>
/// Rule 35: Right Alt as the hotkey must not open menus. In classic Win32 apps, pressing and releasing Alt
/// with nothing in between activates the menu bar, which would then receive the paste. Injecting an
/// unassigned key (`vkE8`, AutoHotkey's default menu-mask key) while Right Alt is down means the release is
/// no longer "Alt alone".
///
/// The injected events carry `LLKHF_INJECTED`, which `HotkeyTranslator` drops, so they never cancel the
/// dictation (rule 28). Nothing happens for Right Ctrl, which opens no menus.
///
/// Called from the dispatcher with every hook event, never from the hook callback: `SendInput` is work, and
/// the callback does none (rule 29). The mask therefore lands a dispatcher turn after the key-down, which is
/// long before any human release; spike S4 confirms it in Notepad and File Explorer.
/// </summary>
public sealed class MenuMask
{
    public const ushort VkMenuMask = 0xE8;

    private readonly Func<HotkeyChoice> choice;
    private bool rightAltDown;

    /// <param name="choice">The hotkey currently in effect.</param>
    public MenuMask(Func<HotkeyChoice> choice) => this.choice = choice;

    public void Observe(RawKeyEvent e)
    {
        if ((e.Flags & HotkeyTranslator.LlkhfInjected) != 0 || !IsRightAlt(e)) return;
        if (e.IsKeyUp)
        {
            rightAltDown = false;
            return;
        }
        // Autorepeat: one mask per press is enough.
        if (rightAltDown) return;
        rightAltDown = true;
        if (choice() == HotkeyChoice.RightAlt) Inject();
    }

    private static bool IsRightAlt(RawKeyEvent e) =>
        e.VkCode == HotkeyTranslator.VkRMenu
        || (e.VkCode == HotkeyTranslator.VkMenu && (e.Flags & HotkeyTranslator.LlkhfExtended) != 0);

    private static void Inject()
    {
        HookInterop.INPUT[] inputs =
        [
            new() { type = HookInterop.INPUT_KEYBOARD, u = new() { ki = new() { wVk = VkMenuMask } } },
            new() { type = HookInterop.INPUT_KEYBOARD, u = new() { ki = new() { wVk = VkMenuMask, dwFlags = HookInterop.KEYEVENTF_KEYUP } } },
        ];
        var sent = HookInterop.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<HookInterop.INPUT>());
        if (sent != inputs.Length)
        {
            // Blocked by UIPI when an elevated window has focus; the menu may open, the dictation is unaffected.
            HookLog.Error("hotkey", $"menu mask: SendInput sent {sent} of {inputs.Length}: error {Marshal.GetLastPInvokeError()}");
        }
    }
}

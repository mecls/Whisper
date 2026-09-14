namespace Spit.Core.Tests;

// Port of mac/VoiceTests/HotkeyInterpreterTests.swift with Windows keys (rule 27). Names are the
// Mac's: Fn (the Mac default) maps to Right Ctrl (the Windows default), Right Option to Right Alt, and
// macOS key codes to virtual-key codes (F5 96 → 0x74, Esc 53 → 0x1B, A 0 → 0x41, S 1 → 0x53,
// Return 36 → 0x0D, Space 49 → 0x20, Left 123 → 0x25).
public sealed class HotkeyInterpreterTests
{
    private static KeyEvent Flags(bool rightCtrl = false, bool rightAlt = false) => new KeyEvent.Flags(rightCtrl, rightAlt);
    private static KeyEvent KeyDown(int vk) => new KeyEvent.KeyDown(vk);

    [Fact]
    public void testFnHoldAndRelease()
    {
        var i = new HotkeyInterpreter(HotkeyChoice.RightCtrl);
        Assert.Equal(HotkeyAction.Press, i.Handle(Flags(rightCtrl: true)));
        Assert.Equal(HotkeyAction.Release, i.Handle(Flags()));
    }

    [Fact]
    public void testOtherKeyWhileHeldCancelsAndSwallowsTheRelease()
    {
        var i = new HotkeyInterpreter(HotkeyChoice.RightCtrl);
        _ = i.Handle(Flags(rightCtrl: true));
        Assert.Equal(HotkeyAction.Cancel, i.Handle(KeyDown(0x74)));   // F5
        Assert.Null(i.Handle(Flags()));
        Assert.Equal(HotkeyAction.Press, i.Handle(Flags(rightCtrl: true)));
    }

    [Fact]
    public void testEscapeCancels()
    {
        var i = new HotkeyInterpreter(HotkeyChoice.RightCtrl);
        _ = i.Handle(Flags(rightCtrl: true));
        Assert.Equal(HotkeyAction.Cancel, i.Handle(KeyDown(0x1B)));
    }

    [Fact]
    public void testKeysWhileIdleAreIgnored()
    {
        var i = new HotkeyInterpreter(HotkeyChoice.RightCtrl);
        Assert.Null(i.Handle(KeyDown(0x41)));
        Assert.Null(i.Handle(Flags(rightAlt: true)));
    }

    [Fact]
    public void testRightOptionChoiceIgnoresFn()
    {
        var i = new HotkeyInterpreter(HotkeyChoice.RightAlt);
        Assert.Null(i.Handle(Flags(rightCtrl: true)));
        Assert.Equal(HotkeyAction.Press, i.Handle(Flags(rightAlt: true)));
        Assert.Equal(HotkeyAction.Release, i.Handle(Flags()));
    }

    // D2: IsHeld is true after press, false after release — the monitor relies on this to decide
    // whether a Settings hotkey change must be deferred to the next release/cancel.
    [Fact]
    public void testIsHeldReflectsPressAndRelease()
    {
        var i = new HotkeyInterpreter(HotkeyChoice.RightCtrl);
        Assert.False(i.IsHeld);
        _ = i.Handle(Flags(rightCtrl: true));
        Assert.True(i.IsHeld);
        _ = i.Handle(Flags());
        Assert.False(i.IsHeld);
    }

    // H1: press → Esc (cancel) resets IsHeld immediately — it must not wait for the physical release
    // that follows, which is swallowed and produces no further action.
    [Fact]
    public void testIsHeldResetsOnCancelBeforeThePhysicalRelease()
    {
        var i = new HotkeyInterpreter(HotkeyChoice.RightCtrl);
        _ = i.Handle(Flags(rightCtrl: true));
        Assert.True(i.IsHeld);
        Assert.Equal(HotkeyAction.Cancel, i.Handle(KeyDown(HotkeyInterpreter.EscapeKeyCode)));
        Assert.False(i.IsHeld);
        Assert.Null(i.Handle(Flags()));
        Assert.False(i.IsHeld);
    }

    // MARK: - Latched mode (prd-hands-free-dictation.md rule 6)

    [Fact]
    public void testALatchedSessionIgnoresOrdinaryKeystrokes()
    {
        // The entire point of hands-free is that the user keeps typing.
        var i = new HotkeyInterpreter(HotkeyChoice.RightCtrl) { Latched = true };
        foreach (var vk in new[] { 0x41, 0x53, 0x0D, 0x20, 0x25 })   // a, s, return, space, left-arrow
        {
            Assert.Null(i.Handle(KeyDown(vk)));   // must not cancel a latched session
        }
    }

    [Fact]
    public void testEscStillCancelsALatchedSession()
    {
        // The single exception: once the key is no longer held there is no other way to say "throw this away".
        var i = new HotkeyInterpreter(HotkeyChoice.RightCtrl) { Latched = true };
        Assert.Equal(HotkeyAction.Cancel, i.Handle(KeyDown(HotkeyInterpreter.EscapeKeyCode)));
    }

    [Fact]
    public void testUnlatchedBehaviourIsUnchanged()
    {
        // The hold path must be untouched: during a hold, any key is a shortcut and cancels.
        var i = new HotkeyInterpreter(HotkeyChoice.RightCtrl);
        Assert.Equal(HotkeyAction.Press, i.Handle(Flags(rightCtrl: true)));
        Assert.Equal(HotkeyAction.Cancel, i.Handle(KeyDown(0x41)));
    }
}

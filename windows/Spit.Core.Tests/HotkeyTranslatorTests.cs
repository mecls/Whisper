namespace Spit.Core.Tests;

/// Rules 27 and 28, and build spec AC-4: raw hook events through the translator and the interpreter,
/// the way the hook thread feeds them. Not a Mac port — the Mac has no autorepeat on `flagsChanged`,
/// no AltGr and no injected-event flag.
public sealed class HotkeyTranslatorTests
{
    private const int Extended = HotkeyTranslator.LlkhfExtended;
    private const int InjectedFlag = HotkeyTranslator.LlkhfInjected;

    private static RawKeyEvent Down(int vk, int scan, int flags = 0) => new(vk, scan, flags, IsKeyUp: false);
    private static RawKeyEvent Up(int vk, int scan, int flags = 0) => new(vk, scan, flags | HotkeyTranslator.LlkhfUp, IsKeyUp: true);

    private static readonly RawKeyEvent RightCtrlDown = Down(HotkeyTranslator.VkRControl, 0x1D, Extended);
    private static readonly RawKeyEvent RightCtrlUp = Up(HotkeyTranslator.VkRControl, 0x1D, Extended);
    private static readonly RawKeyEvent RightAltDown = Down(HotkeyTranslator.VkRMenu, 0x38, Extended);
    private static readonly RawKeyEvent RightAltUp = Up(HotkeyTranslator.VkRMenu, 0x38, Extended);
    private static readonly RawKeyEvent FakeLeftCtrlDown = Down(0xA2, HotkeyTranslator.ScanCodeFakeLeftCtrl);
    private static readonly RawKeyEvent FakeLeftCtrlUp = Up(0xA2, HotkeyTranslator.ScanCodeFakeLeftCtrl);

    private sealed class Pipeline(HotkeyChoice choice)
    {
        public HotkeyTranslator Translator { get; } = new(choice);
        public HotkeyInterpreter Interpreter { get; } = new(choice);
        public HotkeyAction? Feed(RawKeyEvent e) => Translator.Translate(e) is { } k ? Interpreter.Handle(k) : null;
    }

    [Fact]
    public void testAutorepeatOfTheHeldHotkeyIsNotAnotherKey()
    {
        var p = new Pipeline(HotkeyChoice.RightCtrl);
        Assert.Equal(HotkeyAction.Press, p.Feed(RightCtrlDown));
        for (var n = 0; n < 5; n++)
        {
            Assert.Null(p.Translator.Translate(RightCtrlDown));
        }
        Assert.True(p.Interpreter.IsHeld);
        Assert.Equal(HotkeyAction.Release, p.Feed(RightCtrlUp));
    }

    [Fact]
    public void testAltGrsFakeLeftCtrlIsNotAnotherKey()
    {
        // Right Ctrl held, then AltGr pressed: its fake Left Ctrl must not cancel.
        var ctrl = new Pipeline(HotkeyChoice.RightCtrl);
        Assert.Equal(HotkeyAction.Press, ctrl.Feed(RightCtrlDown));
        Assert.Null(ctrl.Translator.Translate(FakeLeftCtrlDown));
        Assert.Null(ctrl.Translator.Translate(FakeLeftCtrlUp));
        Assert.Equal(HotkeyAction.Release, ctrl.Feed(RightCtrlUp));

        // Right Alt as the hotkey on pt-PT: every press arrives as fake Left Ctrl + Right Alt, repeating both.
        var alt = new Pipeline(HotkeyChoice.RightAlt);
        Assert.Null(alt.Feed(FakeLeftCtrlDown));
        Assert.Equal(HotkeyAction.Press, alt.Feed(RightAltDown));
        Assert.Null(alt.Feed(FakeLeftCtrlDown));
        Assert.Null(alt.Feed(RightAltDown));
        Assert.Null(alt.Feed(FakeLeftCtrlUp));
        Assert.Equal(HotkeyAction.Release, alt.Feed(RightAltUp));
    }

    [Fact]
    public void testInjectedKeysAreNotAnotherKey()
    {
        // Spit's own Ctrl+V, and the vkE8 menu mask injected while Right Alt is held (rule 35).
        var p = new Pipeline(HotkeyChoice.RightCtrl);
        Assert.Equal(HotkeyAction.Press, p.Feed(RightCtrlDown));
        Assert.Null(p.Translator.Translate(Down(0xA2, 0x1D, InjectedFlag)));
        Assert.Null(p.Translator.Translate(Down(0x56, 0x2F, InjectedFlag)));   // V
        Assert.Null(p.Translator.Translate(Down(0xE8, 0, InjectedFlag)));
        Assert.True(p.Interpreter.IsHeld);
        Assert.Equal(HotkeyAction.Release, p.Feed(RightCtrlUp));
    }

    [Fact]
    public void testRightCtrlReportedAsControlWithTheExtendedFlagIsTheHotkey()
    {
        var p = new Pipeline(HotkeyChoice.RightCtrl);
        Assert.Null(p.Feed(Down(HotkeyTranslator.VkControl, 0x1D)));   // Left Ctrl: no extended flag
        Assert.Null(p.Feed(Up(HotkeyTranslator.VkControl, 0x1D)));
        Assert.Equal(HotkeyAction.Press, p.Feed(Down(HotkeyTranslator.VkControl, 0x1D, Extended)));
        Assert.Equal(HotkeyAction.Release, p.Feed(Up(HotkeyTranslator.VkControl, 0x1D, Extended)));
    }

    [Fact]
    public void testRightAltReportedAsMenuWithTheExtendedFlagIsTheHotkey()
    {
        var p = new Pipeline(HotkeyChoice.RightAlt);
        Assert.Null(p.Feed(RightCtrlDown));                              // not the chosen key
        Assert.Null(p.Feed(RightCtrlUp));
        Assert.Null(p.Feed(Down(HotkeyTranslator.VkMenu, 0x38)));        // Left Alt
        Assert.Null(p.Feed(Up(HotkeyTranslator.VkMenu, 0x38)));
        Assert.Equal(HotkeyAction.Press, p.Feed(Down(HotkeyTranslator.VkMenu, 0x38, Extended)));
        Assert.Equal(HotkeyAction.Release, p.Feed(Up(HotkeyTranslator.VkMenu, 0x38, Extended)));
    }

    [Fact]
    public void testRightCtrlPlusCCancels()
    {
        var p = new Pipeline(HotkeyChoice.RightCtrl);
        Assert.Equal(HotkeyAction.Press, p.Feed(RightCtrlDown));
        Assert.Equal(HotkeyAction.Cancel, p.Feed(Down(0x43, 0x2E)));     // C
        Assert.Null(p.Feed(Up(0x43, 0x2E)));
        Assert.Null(p.Feed(RightCtrlUp));                                 // the release after a cancel is swallowed
    }

    [Fact]
    public void testEscCancels()
    {
        var p = new Pipeline(HotkeyChoice.RightCtrl);
        Assert.Equal(HotkeyAction.Press, p.Feed(RightCtrlDown));
        Assert.Equal(HotkeyAction.Cancel, p.Feed(Down(HotkeyTranslator.VkEscape, 0x01)));

        var latched = new Pipeline(HotkeyChoice.RightCtrl);
        latched.Interpreter.Latched = true;
        Assert.Null(latched.Feed(Down(0x41, 0x1E)));                     // typing survives a latched session
        Assert.Equal(HotkeyAction.Cancel, latched.Feed(Down(HotkeyTranslator.VkEscape, 0x01)));
    }

    [Fact]
    public void testAModifierWhileHeldIsNotAnotherKeyButTheChordItStartsIs()
    {
        // The Mac sees modifiers as `flagsChanged`, which never cancel; the key that completes a chord does.
        var p = new Pipeline(HotkeyChoice.RightCtrl);
        Assert.Equal(HotkeyAction.Press, p.Feed(RightCtrlDown));
        Assert.Null(p.Feed(Down(0xA0, 0x2A)));                           // Left Shift
        Assert.Null(p.Feed(Up(0xA0, 0x2A)));
        Assert.Equal(HotkeyAction.Release, p.Feed(RightCtrlUp));

        Assert.Equal(HotkeyAction.Press, p.Feed(RightCtrlDown));
        Assert.Null(p.Feed(Down(0xA0, 0x2A)));
        Assert.Equal(HotkeyAction.Cancel, p.Feed(Down(0x25, 0x4B, Extended)));   // Left arrow
    }

    /// AC-4, end to end: a repeated Right Ctrl, AltGr's fake Left Ctrl and Spit's own injected V during
    /// one held dictation produce no cancel, and the release still ends it.
    [Fact]
    public void testRepeatAltGrAndOwnPasteLeaveTheHoldIntact()
    {
        var p = new Pipeline(HotkeyChoice.RightCtrl);
        var actions = new List<HotkeyAction?>
        {
            p.Feed(RightCtrlDown),
            p.Feed(RightCtrlDown),
            p.Feed(FakeLeftCtrlDown),
            p.Feed(Down(0x56, 0x2F, InjectedFlag)),
            p.Feed(Up(0x56, 0x2F, InjectedFlag)),
            p.Feed(RightCtrlDown),
        };
        Assert.DoesNotContain(HotkeyAction.Cancel, actions);
        Assert.Equal(HotkeyAction.Press, actions[0]);
        Assert.Equal(HotkeyAction.Release, p.Feed(RightCtrlUp));
    }
}

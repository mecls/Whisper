using System.Runtime.InteropServices;
using static Spit.App.Tests.ClipboardTestSupport;

namespace Spit.App.Tests;

/// Rules 25, 30 and 34. The paste itself (SendInput into a real window) is left to the PC checklist:
/// on a CI runner it would type into whatever has focus.
[Collection(ClipboardCollection.Name)]
public sealed class TextInjectorTests
{
    [WindowsFact]
    public void CopyToClipboard_SetsTheTextAndTheHistoryExclusionFormat()
    {
        WithOwnerWindow(owner =>
        {
            var injector = new TextInjector(() => owner);

            Assert.True(injector.CopyToClipboard("copy last dictation"));

            var exclude = ClipboardSession.Register(ClipboardSession.ExcludeFromMonitorFormatName);
            Assert.NotEqual(0u, exclude);
            Assert.True(Native.IsClipboardFormatAvailable(exclude));
            byte[]? text = null;
            byte[]? flag = null;
            Assert.True(RunPatiently(owner, "test read", () =>
            {
                text = ClipboardSession.ReadData(Native.CF_UNICODETEXT, ClipboardSnapshot.MaxBytes);
                flag = ClipboardSession.ReadData(exclude, 64);
                return true;
            }));
            Assert.NotNull(text);
            Assert.Equal("copy last dictation", TextOf(text));
            Assert.NotNull(flag);
            Assert.Equal(new byte[4], flag[..4]);
        });
    }

    [WindowsFact]
    public void Route_AnElevatedTargetGoesToTheClipboardWhateverTheApp()
    {
        Assert.Equal(PasteRoute.ClipboardOnlyElevated, TextInjector.Route("notepad.exe", targetElevated: true));
        Assert.Equal(PasteRoute.ClipboardOnlyElevated, TextInjector.Route(null, targetElevated: true));
        Assert.Equal(PasteRoute.ClipboardOnlyElevated, TextInjector.Route(TextInjector.OwnExecutable, targetElevated: true));
    }

    [WindowsFact]
    public void Route_SpitItselfGoesToTheClipboard()
    {
        Assert.Equal(PasteRoute.ClipboardOnlySelf, TextInjector.Route(TextInjector.OwnExecutable, targetElevated: false));
        Assert.Equal(PasteRoute.ClipboardOnlySelf, TextInjector.Route(TextInjector.OwnExecutable.ToUpperInvariant(), targetElevated: false));
    }

    [WindowsFact]
    public void Route_OrdinaryAndUnknownTargetsPaste()
    {
        foreach (var exe in new[] { "notepad.exe", "chrome.exe", "winword.exe", "applicationframehost.exe" })
            Assert.Equal(PasteRoute.Paste, TextInjector.Route(exe, targetElevated: false));
        Assert.Equal(PasteRoute.Paste, TextInjector.Route(null, targetElevated: false));
    }

    [WindowsFact]
    public void InputStructs_HaveTheX64Layout()
    {
        // SendInput rejects a cbSize that isn't sizeof(INPUT), which would fail every paste.
        Assert.Equal(40, Marshal.SizeOf<Native.INPUT>());
        Assert.Equal(24, Marshal.SizeOf<Native.KEYBDINPUT>());
        Assert.Equal(8, (int)Marshal.OffsetOf<Native.INPUT>(nameof(Native.INPUT.u)));
    }
}

namespace Spit.Core.Tests;

/// Port of mac/VoiceTests/TextInjectorRoutingTests.swift (`TextInjector.mustUseClipboard` is
/// `PasteRouting.MustUseClipboard` here). A dictation must never be pasted into Spit itself. The Mac's
/// secure-input cases become elevated-target cases (rules 30, 34): an admin window is the Windows
/// target that cannot receive a synthetic paste.
public sealed class TextInjectorRoutingTests
{
    private static string Own => PasteRouting.OwnBundleId;

    [Fact]
    public void testDictationIntoVoiceItselfGoesToTheClipboard()
    {
        Assert.True(PasteRouting.MustUseClipboard(targetBundleId: Own, targetIsElevated: false));
    }

    [Fact]
    public void testOrdinaryAppsStillGetAPaste()
    {
        foreach (var bundleId in new[] { "chrome.exe", "slack.exe", "notepad.exe", "code.exe" })
        {
            Assert.False(
                PasteRouting.MustUseClipboard(targetBundleId: bundleId, targetIsElevated: false),
                $"{bundleId} must still receive a normal paste");
        }
    }

    [Fact]
    public void testAnUnknownTargetPastesRatherThanBeingTreatedAsVoice()
    {
        // The foreground context can be unknown (no foreground window, access denied on the image name).
        // Reading "I don't know" as "it might be me" would divert ordinary dictations to the clipboard.
        Assert.False(PasteRouting.MustUseClipboard(targetBundleId: null, targetIsElevated: false));
    }

    [Fact]
    public void testSecureInputStillWinsForEveryTarget()
    {
        // Windows' counterpart of secure input: an elevated target cannot receive SendInput at all,
        // whichever app owns it, and the failure is silent.
        Assert.True(PasteRouting.MustUseClipboard(targetBundleId: null, targetIsElevated: true));
        Assert.True(PasteRouting.MustUseClipboard(targetBundleId: "windowsterminal.exe", targetIsElevated: true));
        Assert.True(PasteRouting.MustUseClipboard(targetBundleId: Own, targetIsElevated: true));
    }

    [Fact]
    public void testOwnBundleIdIsResolvedFromTheBundleNotHardcodedTwice()
    {
        // One literal, in the lowercased executable-name form `FrontmostApp.BundleId` carries (rule 31).
        Assert.Equal("spit.exe", Own);
    }
}

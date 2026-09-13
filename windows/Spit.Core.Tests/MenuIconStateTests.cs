namespace Spit.Core.Tests;

/// Rule 37: the tray icon ranks exactly like the Mac's `menuIcon` — unauthorized > latched > listening > mic.
public sealed class MenuIconStateTests
{
    public static TheoryData<HUDState> EveryHudState =>
    [
        new HUDState.Hidden(),
        new HUDState.Listening(),
        new HUDState.Transcribing(0.5),
        new HUDState.Cleaning(),
        new HUDState.Done("x", null),
        new HUDState.Message(Strings.NothingHeard),
        new HUDState.ModelLoading(0.2),
    ];

    [Theory]
    [MemberData(nameof(EveryHudState))]
    public void UnauthorizedOutranksEverything(HUDState hud)
    {
        Assert.Equal(MenuIconState.MicCrossed, MenuIcon.For(unauthorized: true, latched: true, hud));
        Assert.Equal(MenuIconState.MicCrossed, MenuIcon.For(unauthorized: true, latched: false, hud));
    }

    [Theory]
    [MemberData(nameof(EveryHudState))]
    public void LatchedOutranksListening(HUDState hud)
    {
        Assert.Equal(MenuIconState.Recording, MenuIcon.For(unauthorized: false, latched: true, hud));
    }

    [Fact]
    public void ListeningFillsTheMic()
    {
        Assert.Equal(MenuIconState.MicFilled, MenuIcon.For(unauthorized: false, latched: false, new HUDState.Listening()));
    }

    [Theory]
    [MemberData(nameof(EveryHudState))]
    public void EverythingElseIsThePlainMic(HUDState hud)
    {
        if (hud is HUDState.Listening) return;
        Assert.Equal(MenuIconState.Mic, MenuIcon.For(unauthorized: false, latched: false, hud));
    }
}

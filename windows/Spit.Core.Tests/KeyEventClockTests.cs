namespace Spit.Core.Tests;

/// Gesture timing follows when the keys moved, not when the dispatcher got to them. Not a Mac port: the Mac's
/// event tap delivers on the main run loop without a capture start in the way.
public sealed class KeyEventClockTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    /// The wall clock at a given tick, so hook stamps and handling times share one timeline.
    private static DateTimeOffset At(uint eventTick, uint handledTick) =>
        KeyEventClock.EventTime(eventTick, handledTick, Base.AddMilliseconds(handledTick));

    [Fact]
    public void testAnEventIsPlacedWhenItHappenedNotWhenItWasHandled()
    {
        Assert.Equal(Base.AddMilliseconds(10_000), At(eventTick: 10_000, handledTick: 11_200));
    }

    [Fact]
    public void testTheTickCounterWrappingKeepsTheRealAge()
    {
        var now = Base;
        Assert.Equal(now.AddMilliseconds(-200), KeyEventClock.EventTime(eventTick: uint.MaxValue - 99, nowTick: 100, now));
    }

    [Fact]
    public void testAnUnknownFutureOrStaleStampFallsBackToNow()
    {
        Assert.Equal(Base, KeyEventClock.EventTime(0, 5_000, Base));
        Assert.Equal(Base, KeyEventClock.EventTime(5_001, 5_000, Base));
        Assert.Equal(Base, KeyEventClock.EventTime(1_000, 1_000 + KeyEventClock.MaxAgeMs + 1, Base));
    }

    [Fact]
    public void testATapHandledLateBehindAColdMicrophoneStillLatches()
    {
        // A 150 ms tap and a second press 200 ms after it. The first press is handled at once; starting a cold
        // microphone then blocks the dispatcher, so the release and the second press are handled at 51.5 s.
        const uint press = 50_000, release = 50_150, second = 50_350, handled = 51_500;

        var latch = new TapLatch();
        Assert.Equal(new[] { TapLatch.Outcome.StartDictation }, latch.Handle(HotkeyAction.Press, At(press, press + 5)));
        Assert.Equal(new[] { TapLatch.Outcome.HoldOpen }, latch.Handle(HotkeyAction.Release, At(release, handled)));
        Assert.Equal(new[] { TapLatch.Outcome.Latch }, latch.Handle(HotkeyAction.Press, At(second, handled)));

        // Measured by handling time instead, the same tap reads as a 1.5 s hold and ends the dictation.
        var byHandlingTime = new TapLatch();
        byHandlingTime.Handle(HotkeyAction.Press, Base.AddMilliseconds(press + 5));
        Assert.Equal(new[] { TapLatch.Outcome.EndSession }, byHandlingTime.Handle(HotkeyAction.Release, Base.AddMilliseconds(handled)));
    }

    [Fact]
    public void testTheHookStampRidesTheRawEventAndDefaultsToUnknown()
    {
        var down = new RawKeyEvent(HotkeyTranslator.VkRControl, 0x1D, HotkeyTranslator.LlkhfExtended, IsKeyUp: false);
        Assert.Equal(0u, down.Time);
        Assert.Equal(1_234u, (down with { Time = 1_234 }).Time);
    }
}

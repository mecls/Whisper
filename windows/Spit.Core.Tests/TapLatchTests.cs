using static Spit.Core.TapLatch.Outcome;

namespace Spit.Core.Tests;

/// Port of mac/VoiceTests/TapLatchTests.swift: prd-hands-free-dictation.md §5's gesture table, and the
/// boundary that table depends on. Time is supplied explicitly rather than slept through.
public sealed class TapLatchTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
    private static DateTimeOffset At(int ms) => T0.AddMilliseconds(ms);
    private static TapLatch.Outcome[] O(params TapLatch.Outcome[] outcomes) => outcomes;

    // MARK: - The table

    [Fact]
    public void testAHoldStopsOnReleaseAndNeverLatches()
    {
        // A real dictation. The release must stop it immediately: no window, no timer.
        var latch = new TapLatch();
        Assert.Equal(O(StartDictation), latch.Handle(HotkeyAction.Press, At(0)));
        Assert.Equal(O(EndSession), latch.Handle(HotkeyAction.Release, At(600)));
        Assert.False(latch.IsLatched);
    }

    [Fact]
    public void testTapThenTapInsideTheWindowLatches()
    {
        var latch = new TapLatch();
        Assert.Equal(O(StartDictation), latch.Handle(HotkeyAction.Press, At(0)));
        Assert.Equal(O(HoldOpen), latch.Handle(HotkeyAction.Release, At(150)));
        Assert.Equal(O(Latch), latch.Handle(HotkeyAction.Press, At(200)));
        Assert.True(latch.IsLatched);
    }

    [Fact]
    public void testTapThenTapAfterTheWindowDoesNotLatch()
    {
        // 150 ms release opens a window until 450 ms; a press at 550 ms is a separate gesture.
        var latch = new TapLatch();
        _ = latch.Handle(HotkeyAction.Press, At(0));
        _ = latch.Handle(HotkeyAction.Release, At(150));
        Assert.Equal(O(Abandon, StartDictation), latch.Handle(HotkeyAction.Press, At(550)));
        Assert.False(latch.IsLatched);
    }

    [Fact]
    public void testAWindowThatExpiresAbandonsTheRecording()
    {
        var latch = new TapLatch();
        _ = latch.Handle(HotkeyAction.Press, At(0));
        _ = latch.Handle(HotkeyAction.Release, At(150));
        Assert.Equal(O(Abandon), latch.WindowExpired(At(450)));
        Assert.False(latch.IsActive);
    }

    [Fact]
    public void testALatchedSessionEndsOnKeyDownAndSwallowsTheRelease()
    {
        var latch = new TapLatch();
        _ = latch.Handle(HotkeyAction.Press, At(0));
        _ = latch.Handle(HotkeyAction.Release, At(150));
        _ = latch.Handle(HotkeyAction.Press, At(200));
        Assert.Empty(latch.Handle(HotkeyAction.Release, At(260)));          // the latching press's own release means nothing

        Assert.Equal(O(EndSession), latch.Handle(HotkeyAction.Press, At(30_000))); // ends on key-down, not on release
        Assert.Empty(latch.Handle(HotkeyAction.Release, At(30_080)));       // and its release must not start anything
        Assert.False(latch.IsActive);
    }

    [Fact]
    public void testCancelClearsAnyGesture()
    {
        // Esc during a latched session, and a shortcut chord during a hold. Both discard.
        Action<TapLatch>[] setUps =
        [
            l => _ = l.Handle(HotkeyAction.Press, At(0)),
            l =>
            {
                _ = l.Handle(HotkeyAction.Press, At(0));
                _ = l.Handle(HotkeyAction.Release, At(150));
                _ = l.Handle(HotkeyAction.Press, At(200));
            },
        ];
        foreach (var setUp in setUps)
        {
            var latch = new TapLatch();
            setUp(latch);
            Assert.Empty(latch.Handle(HotkeyAction.Cancel, At(5000)));
            Assert.False(latch.IsActive);
            Assert.False(latch.IsLatched);
        }
    }

    // MARK: - The boundary

    [Fact]
    public void testTheTapThresholdIsTheReducersMinimum()
    {
        // 399 ms is a tap, 401 ms is a dictation. Asserted against the constant rather than a literal
        // 400, so the two definitions cannot silently diverge.
        var minimum = DictationMachine.MinimumMs;

        var @short = new TapLatch();
        _ = @short.Handle(HotkeyAction.Press, At(0));
        Assert.Equal(O(HoldOpen), @short.Handle(HotkeyAction.Release, At(minimum - 1))); // just under the minimum must open a window

        var @long = new TapLatch();
        _ = @long.Handle(HotkeyAction.Press, At(0));
        Assert.Equal(O(EndSession), @long.Handle(HotkeyAction.Release, At(minimum + 1)));
    }

    [Fact]
    public void testExactlyTheMinimumCountsAsAHold()
    {
        var latch = new TapLatch();
        _ = latch.Handle(HotkeyAction.Press, At(0));
        Assert.Equal(O(EndSession), latch.Handle(HotkeyAction.Release, At(DictationMachine.MinimumMs)));
    }

    [Fact]
    public void testAPressExactlyOnTheDeadlineStillLatches()
    {
        // The window is inclusive: a double-tap at exactly 300 ms meant to latch.
        var latch = new TapLatch();
        _ = latch.Handle(HotkeyAction.Press, At(0));
        _ = latch.Handle(HotkeyAction.Release, At(100));
        Assert.Equal(O(Latch), latch.Handle(HotkeyAction.Press, At(100 + TapLatch.WindowMs)));
    }

    // MARK: - Things that should not happen, but must not misbehave

    [Fact]
    public void testWindowExpiredIsIgnoredWhenNoWindowIsOpen()
    {
        // The timer can fire after a second press has already latched; a cancellation that loses a race
        // must not end the session the user is mid-way through.
        var latch = new TapLatch();
        _ = latch.Handle(HotkeyAction.Press, At(0));
        _ = latch.Handle(HotkeyAction.Release, At(150));
        _ = latch.Handle(HotkeyAction.Press, At(200));
        Assert.Empty(latch.WindowExpired(At(450)));   // a late timer must not abandon a latched session
        Assert.True(latch.IsLatched);
    }

    [Fact]
    public void testStrayReleaseAndDoublePressAreInert()
    {
        var latch = new TapLatch();
        Assert.Empty(latch.Handle(HotkeyAction.Release, At(0)));   // a release with no press is meaningless
        _ = latch.Handle(HotkeyAction.Press, At(10));
        Assert.Empty(latch.Handle(HotkeyAction.Press, At(20)));    // the key cannot go down twice without coming up
    }
}

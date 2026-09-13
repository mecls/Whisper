namespace Spit.Core;

/// When a key really moved, from the hook's own timestamp. The dispatcher can reach a key event a second
/// late — a cold microphone blocks it while capture starts — and `TapLatch` measured against the moment the
/// event was handled reads a 150 ms tap as a hold, so a hands-free session could never latch.
public static class KeyEventClock
{
    /// Older than this and the stamp is not trusted: it is not a hook stamp, or the counter moved oddly.
    public const uint MaxAgeMs = 10_000;

    /// `eventTick` and `nowTick` are GetTickCount milliseconds (`KBDLLHOOKSTRUCT.time`, `Environment.TickCount`),
    /// `now` the wall clock read with `nowTick`. The subtraction wraps with the 49.7-day counter. An unknown
    /// stamp (0), one from the future or one implausibly old gives `now`.
    public static DateTimeOffset EventTime(uint eventTick, uint nowTick, DateTimeOffset now)
    {
        if (eventTick == 0) return now;
        var age = unchecked(nowTick - eventTick);
        return age > MaxAgeMs ? now : now - TimeSpan.FromMilliseconds(age);
    }
}

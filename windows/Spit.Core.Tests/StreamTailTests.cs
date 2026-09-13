namespace Spit.Core.Tests;

/// Port of mac/VoiceTests/StreamTailTests.swift (`Coordinator.tail` is `StreamTail.Tail` here).
///
/// The audio a streamed dictation leaves untranscribed, and whether it is worth a pass. The first
/// measured dictation left 4396 ms of 15295 ms untranscribed. Both guards exist to stop the fix being
/// worse than the bug: Whisper hallucinates on very short clips and on silence.
public sealed class StreamTailTests
{
    /// 16 kHz, loud enough to clear `EnergyGate`'s 0.01 threshold comfortably.
    private static float[] Speech(int ms) =>
        Enumerable.Range(0, 16 * ms).Select(i => 0.2f * MathF.Sin(i * 0.1f)).ToArray();

    private static float[] Silence(int ms) =>
        Enumerable.Range(0, 16 * ms).Select(_ => (float)(Random.Shared.NextDouble() * 0.001 - 0.0005)).ToArray();

    [Fact]
    public void testTheUntranscribedTailIsReturnedForAFinalPass()
    {
        // The real shape: 15295 ms recorded, the stream reached 10899 ms.
        var samples = Speech(15295);
        var tail = StreamTail.Tail(samples, afterMs: 10899, totalMs: 15295);
        Assert.NotNull(tail);   // 4396 ms of speech went untranscribed; it must be passed on, not dropped.
        // 4396 ms of new audio plus the overlap, which starts the pass before the boundary.
        var expectedMs = (double)(15295 - (10899 - StreamTail.OverlapMs));
        Assert.Equal(samples.Length * expectedMs / 15295, tail.Length, tolerance: 4.0);
    }

    /// The pass must start before the boundary, or the word straddling it is sliced in half.
    [Fact]
    public void testThePassStartsBeforeTheBoundarySoNoWordIsSplit()
    {
        var samples = Speech(10000);
        var tail = StreamTail.Tail(samples, afterMs: 8000, totalMs: 10000);
        Assert.NotNull(tail);
        var butted = (double)samples.Length * 2000 / 10000;
        Assert.True(tail.Length > butted,
            "The tail starts exactly at the boundary, so the word crossing it is cut in half.");
        Assert.Equal((double)samples.Length * 3500 / 10000, tail.Length, tolerance: 4.0);
    }

    /// The overlap must not turn a gap too small to matter into one worth transcribing.
    [Fact]
    public void testTheOverlapDoesNotResurrectATrivialGap()
    {
        var samples = Speech(5000);
        Assert.Null(StreamTail.Tail(samples, afterMs: 4800, totalMs: 5000));
    }

    /// A boundary earlier than the overlap must clamp to the start rather than index negatively.
    [Fact]
    public void testABoundaryInsideTheOverlapClampsToTheStart()
    {
        var samples = Speech(4000);
        var tail = StreamTail.Tail(samples, afterMs: 500, totalMs: 4000);
        Assert.NotNull(tail);
        Assert.Equal(samples.Length, tail.Length);   // clamped to zero, so the whole recording is passed
    }

    [Fact]
    public void testAGapTooShortToBeAWordIsNotWorthAPass()
    {
        var samples = Speech(5000);
        // 200 ms is a rounding error, and a clip that short is what Whisper hallucinates on.
        Assert.Null(StreamTail.Tail(samples, afterMs: 4800, totalMs: 5000));
        // 500 ms is over the threshold and can hold a word.
        Assert.NotNull(StreamTail.Tail(samples, afterMs: 4500, totalMs: 5000));
    }

    /// The ordinary case of stopping talking a beat before letting go of the key.
    [Fact]
    public void testASilentGapIsNotTranscribed()
    {
        float[] samples = [.. Speech(5000), .. Silence(2000)];
        // The overlap reaches back into the speech before the gap, so this only holds if the decision
        // is made on the new audio alone.
        Assert.Null(StreamTail.Tail(samples, afterMs: 5000, totalMs: 7000));
    }

    [Fact]
    public void testNothingLeftOverProducesNoPass()
    {
        var samples = Speech(5000);
        Assert.Null(StreamTail.Tail(samples, afterMs: 5000, totalMs: 5000));
    }

    /// The stream can report a segment end slightly past the recorded length; that must not index past the buffer.
    [Fact]
    public void testAnOverrunningCoveredLengthIsSafe()
    {
        var samples = Speech(5000);
        Assert.Null(StreamTail.Tail(samples, afterMs: 6000, totalMs: 5000));
        Assert.Null(StreamTail.Tail([], afterMs: 0, totalMs: 5000));
        Assert.Null(StreamTail.Tail(samples, afterMs: 0, totalMs: 0));
    }
}

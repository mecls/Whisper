namespace Spit.Core.Tests;

/// `StreamTail.Combine`: a Windows-only rule, so it lives outside the ported `StreamTailTests`.
public sealed class StreamTailCombineTests
{
    [Fact]
    public void AStreamThatCoveredLessThanTheOverlapIsReplacedByTheTail()
    {
        // The CI smoke run: two streamed words, then a tail pass from the first sample.
        var text = StreamTail.Combine("Hi Joel", coveredMs: 1200, "Hi Joel, quick update. The dashboard is running.");

        Assert.Equal("Hi Joel, quick update. The dashboard is running.", text);
    }

    [Fact]
    public void AStreamCoveringExactlyTheOverlapIsStillReplaced()
    {
        Assert.Equal("one two three", StreamTail.Combine("one", StreamTail.OverlapMs, "one two three"));
    }

    [Fact]
    public void AStreamPastTheOverlapIsStitchedAtTheSeam()
    {
        var text = StreamTail.Combine(
            "we should ship the build on friday after the review",
            coveredMs: 6000,
            "friday after the review and then tell everyone");

        Assert.Equal("we should ship the build on friday after the review and then tell everyone", text);
    }

    [Fact]
    public void AnEmptyTailKeepsTheStreamedText()
    {
        Assert.Equal("Hi Joel", StreamTail.Combine(" Hi Joel ", coveredMs: 900, "  "));
    }

    [Theory]
    [InlineData(1501)]
    [InlineData(1600)]
    [InlineData(2400)]
    [InlineData(9000)]
    public void AStreamTooShortToStitchCountsAsCoveringNothing(int coveredMs)
    {
        // The third review's cases: two streamed words past the overlap used to stitch by appending.
        var covered = StreamTail.EffectiveCoveredMs("Hi Joel,", coveredMs);
        var text = StreamTail.Combine("Hi Joel,", covered, "Hi Joel, quick update.");

        Assert.Equal(0, covered);
        Assert.Equal("Hi Joel, quick update.", text);
    }

    [Fact]
    public void AStreamLongEnoughToStitchKeepsItsCoverage()
    {
        Assert.Equal(2400, StreamTail.EffectiveCoveredMs("we should ship it", 2400));
    }

    [Fact]
    public void ACoveredNothingTailIsTheWholeRecording()
    {
        var samples = new float[16_000 * 3];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(0.3 * Math.Sin(i * 0.05));

        var tail = StreamTail.Tail(samples, StreamTail.EffectiveCoveredMs("Hi Joel,", 2400), 3000);

        Assert.NotNull(tail);
        Assert.Equal(samples.Length, tail.Length);
    }

    [Fact]
    public void ReplacingNeverDuplicatesTheOpeningWords()
    {
        var text = StreamTail.Combine("Hi Joel", coveredMs: 1400, "Hi Joel, quick update.");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "Joel"));
    }
}

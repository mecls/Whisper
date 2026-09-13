namespace Spit.Core.Tests;

/// `StreamTail.Combine` and `Stitch.TryJoin`: Windows-only rules, so they live outside the ported classes.
public sealed class StreamTailCombineTests
{
    [Fact]
    public void AStreamThatCoveredNoMoreThanTheOverlapIsReplacedByTheTail()
    {
        // The CI smoke run: two streamed words, then a tail pass from the first sample.
        Assert.Equal("Hi Joel, quick update. The dashboard is running.",
            StreamTail.Combine("Hi Joel", coveredMs: 1200, "Hi Joel, quick update. The dashboard is running."));
        Assert.Equal("one two three", StreamTail.Combine("one", StreamTail.OverlapMs, "one two three"));
    }

    [Fact]
    public void ASeamFoundInTheTextIsStitched()
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
        Assert.Equal("Hi Joel", StreamTail.Combine(" Hi Joel ", coveredMs: 2400, "  "));
    }

    [Theory]
    [InlineData("Hi Joel,", 2400, "Hi Joel, quick update.")]                       // too few words to anchor
    [InlineData("Hi Joel, quick", 2400, "Joel, quick update. The dashboard")]      // three words, no seam
    [InlineData("Hi Joel, quick update.", 2600, "quick update. The dashboard is")] // four words, no seam
    public void WithoutASeamOnlyAWholeRecordingPassIsTrusted(string streamed, int coveredMs, string tail)
    {
        // Each of these pasted words twice when appended (third and fourth reviews).
        Assert.Null(StreamTail.Combine(streamed, coveredMs, tail));
    }

    [Fact]
    public void AStreamThatCoveredTheWholeRecordingRunsNoTailPassAtAll()
    {
        // A short "Thanks Joel." fully streamed must paste at once, with no pass after the key comes up.
        var samples = Tone(seconds: 1.5);

        Assert.Null(StreamTail.Tail(samples, afterMs: 1500, totalMs: 1500));
    }

    [Fact]
    public void TryJoinReportsWhetherItFoundTheSeam()
    {
        Assert.True(Stitch.TryJoin("the build on friday after the review", "friday after the review and more", out var stitched));
        Assert.Equal("the build on friday after the review and more", stitched);

        Assert.False(Stitch.TryJoin("Hi Joel, quick", "Joel, quick update.", out var appended));
        Assert.Equal("Hi Joel, quick Joel, quick update.", appended);
        Assert.Equal(appended, Stitch.Join("Hi Joel, quick", "Joel, quick update."));
    }

    private static float[] Tone(double seconds)
    {
        var samples = new float[(int)(16_000 * seconds)];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(0.3 * Math.Sin(i * 0.05));
        return samples;
    }
}

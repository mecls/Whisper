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

    [Fact]
    public void ReplacingNeverDuplicatesTheOpeningWords()
    {
        var text = StreamTail.Combine("Hi Joel", coveredMs: 1400, "Hi Joel, quick update.");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "Joel"));
    }
}

namespace Spit.Core.Tests;

/// Port of mac/VoiceTests/StitchTests.swift: joining the streamed transcript to the final pass over the
/// audio it did not reach. Every case is a real failure or a real recording.
public sealed class StitchTests
{
    /// The counting test, verbatim: the stream hallucinated past 12 to 29; the tail pass produced 12…20.
    [Fact]
    public void testTheHallucinatedContinuationIsCutAtTheSeam()
    {
        const string streamed = "1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,";
        const string tail = "12, 13, 14, 15, 16, 17, 18, 19, 20.";
        Assert.Equal("1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20.",
            Stitch.Join(streamed, tail));
    }

    /// The ordinary case: the tail is genuinely new speech and must not be thrown away.
    [Fact]
    public void testGenuinelyNewSpeechIsKept()
    {
        Assert.Equal(
            "Let's test the live transcription path and how well it's working, if it's cutting the chunks properly or not.",
            Stitch.Join("Let's test the live transcription path and how well it's working,",
                "if it's cutting the chunks properly or not."));
    }

    /// A short overlap at the seam — the common shape when the timestamp lands mid-phrase.
    [Fact]
    public void testAnOverlappingSeamIsNotRepeated()
    {
        Assert.Equal(
            "it has all the settings and so I think we have this in a very working state.",
            Stitch.Join("it has all the settings and so I think we have",
                "and so I think we have this in a very working state."));
    }

    /// Punctuation and case are not evidence about where the seam is.
    [Fact]
    public void testPunctuationAndCaseDoNotBlockTheMatch()
    {
        Assert.Equal("Yeah, once again, it didn't get part of the text.",
            Stitch.Join("Yeah, Once Again, it didn't get part", "once again, it didn't get part of the text."));
    }

    /// A phrase repeated earlier in the dictation is not the seam.
    [Fact]
    public void testARepeatedPhraseEarlierOnIsNotMistakenForTheSeam()
    {
        Assert.Equal(
            "and then I said and then I said something different.",
            Stitch.Join("and then I said and then I said something else entirely here",
                "and then I said something different."));
    }

    /// Too short to be evidence: a one-word tail is appended rather than used to cut the transcript.
    [Fact]
    public void testAOneWordTailIsAppendedNotMatched()
    {
        Assert.Equal("the quick brown fox the the", Stitch.Join("the quick brown fox the", "the"));
    }

    [Fact]
    public void testEmptyInputs()
    {
        Assert.Equal("Hello there.", Stitch.Join("Hello there.", ""));
        Assert.Equal("Hello there.", Stitch.Join("", "Hello there."));
        Assert.Equal("Hello there.", Stitch.Join("   ", "  Hello there. "));
    }

    /// The seam at the very start: the stream produced only a hallucination, and the tail is the whole transcript.
    [Fact]
    public void testAWhollyOverlappingTailReplacesTheStream()
    {
        Assert.Equal("one two three four five six", Stitch.Join("one two three four", "one two three four five six"));
    }
}

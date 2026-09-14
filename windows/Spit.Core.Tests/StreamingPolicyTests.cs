namespace Spit.Core.Tests;

/// The streaming rules from rule 22's table. Not one of the 14 ported classes; the text cases mirror
/// mac/VoiceTests/StreamingTranscriberTests.swift, whose type is WhisperKit-bound on the Mac.
public sealed class StreamingPolicyTests
{
    private static StreamSegment Seg(string text, float start, float end) => new(start, end, text);

    [Fact]
    public void testConstantsMatchTheMacTable()
    {
        Assert.Equal(2, StreamingPolicy.RequiredSegmentsForConfirmation);
        Assert.Equal(1f, StreamingPolicy.MinimumNewAudioSeconds);
        Assert.Equal(100, StreamingPolicy.PollIntervalMs);
        Assert.Equal(0.3f, StreamingPolicy.SilenceThreshold);
        Assert.Equal(1f / 30f, StreamingPolicy.SpeechFloor);
    }

    [Fact]
    public void testTheUnconfirmedTailIsPasted()
    {
        StreamSegment[] confirmed = [Seg("General shows.", 0, 2), Seg(" I tried the streaming fix.", 2, 5)];
        StreamSegment[] unconfirmed = [Seg(" It's working quite okay.", 5, 7), Seg(" The only problem is it cuts the speech.", 7, 10)];
        var text = StreamingPolicy.FinalText(confirmed, unconfirmed);
        Assert.NotNull(text);
        Assert.Contains("The only problem is it cuts the speech.", text);
        Assert.StartsWith("General shows.", text);
        Assert.Equal(4, StreamingPolicy.SegmentCount(confirmed, unconfirmed));
    }

    [Fact]
    public void testAShortDictationIsStreamedRatherThanFallingBack()
    {
        Assert.Equal("Turn the lights off.", StreamingPolicy.FinalText([], [Seg("Turn the lights off.", 0, 2)]));
    }

    [Fact]
    public void testSilenceStillFallsBack()
    {
        Assert.Null(StreamingPolicy.FinalText([], []));
        Assert.Null(StreamingPolicy.FinalText([Seg("  ", 0, 1)], []));
    }

    [Fact]
    public void testTheJoinDoesNotDoubleSpaceOrRunWordsTogether()
    {
        Assert.Equal("One two. Three four.", StreamingPolicy.FinalText([Seg("One two.", 0, 1)], [Seg("  Three four.", 1, 2)]));
    }

    [Fact]
    public void testCoveredSecondsTracksTheFurthestSegmentAndNeverMovesBack()
    {
        Assert.Equal(8.5, StreamingPolicy.CoveredSeconds(0, [Seg("a", 0, 3)], [Seg("b", 3, 8.5f)]));
        Assert.Equal(9.0, StreamingPolicy.CoveredSeconds(9, [Seg("a", 0, 3)], []));
        Assert.Equal(3.0, StreamingPolicy.CoveredSeconds(0, [Seg("a", 0, 3)], []));
    }

    [Fact]
    public void testAllButTheLastTwoSegmentsAreConfirmed()
    {
        StreamSegment[] pass = [Seg("a", 0, 1), Seg(" b", 1, 2), Seg(" c", 2, 3), Seg(" d", 3, 4)];
        var state = StreamingPolicy.Confirm(StreamConfirmation.Empty, pass);
        Assert.Equal(pass[..2], state.Confirmed);
        Assert.Equal(pass[2..], state.Unconfirmed);
        Assert.Equal(2f, state.LastConfirmedSegmentEndSeconds);
    }

    [Fact]
    public void testTwoOrFewerSegmentsConfirmNothing()
    {
        StreamSegment[] pass = [Seg("a", 0, 1), Seg(" b", 1, 2)];
        var state = StreamingPolicy.Confirm(StreamConfirmation.Empty, pass);
        Assert.Empty(state.Confirmed);
        Assert.Equal(pass, state.Unconfirmed);
        Assert.Equal(0f, state.LastConfirmedSegmentEndSeconds);
    }

    [Fact]
    public void testConfirmedSegmentsAccumulateAcrossPassesOnlyWhenTheyReachFurther()
    {
        var first = StreamingPolicy.Confirm(StreamConfirmation.Empty, [Seg("a", 0, 1), Seg(" b", 1, 2), Seg(" c", 2, 3)]);
        Assert.Equal([Seg("a", 0, 1)], first.Confirmed);

        // The next pass starts at the last confirmed end and confirms further.
        var second = StreamingPolicy.Confirm(first, [Seg(" b", 1, 2), Seg(" c", 2, 3), Seg(" d", 3, 4), Seg(" e", 4, 5)]);
        Assert.Equal([Seg("a", 0, 1), Seg(" b", 1, 2), Seg(" c", 2, 3)], second.Confirmed);
        Assert.Equal([Seg(" d", 3, 4), Seg(" e", 4, 5)], second.Unconfirmed);
        Assert.Equal(3f, second.LastConfirmedSegmentEndSeconds);

        // A pass whose confirmable part ends no later than what is settled adds nothing, but still
        // replaces the tail.
        var third = StreamingPolicy.Confirm(second, [Seg(" c", 2, 3), Seg(" d2", 3, 4.5f), Seg(" e2", 4.5f, 5.5f)]);
        Assert.Equal(second.Confirmed, third.Confirmed);
        Assert.Equal([Seg(" d2", 3, 4.5f), Seg(" e2", 4.5f, 5.5f)], third.Unconfirmed);
        Assert.Equal(3f, third.LastConfirmedSegmentEndSeconds);
    }

    [Fact]
    public void testAPassNeedsMoreThanOneSecondOfNewAudio()
    {
        float[] loud = [.. Enumerable.Repeat(1f, 20)];
        Assert.False(StreamingPolicy.ShouldRunPass(bufferedSamples: 32000, lastBufferSize: 16000, loud));   // exactly 1 s
        Assert.True(StreamingPolicy.ShouldRunPass(bufferedSamples: 32001, lastBufferSize: 16000, loud));
        Assert.False(StreamingPolicy.ShouldRunPass(bufferedSamples: 32001, lastBufferSize: 16000, [.. Enumerable.Repeat(0.1f, 20)]));
    }

    [Fact]
    public void testRelativeEnergyPutsTheSilenceCutoffAtTheEnergyGateThreshold()
    {
        Assert.Equal(StreamingPolicy.SilenceThreshold, StreamingPolicy.RelativeEnergy(EnergyGate.DefaultThreshold), 1e-6f);
        Assert.Equal(1f, StreamingPolicy.RelativeEnergy(0.5f));
        Assert.Equal(0f, StreamingPolicy.RelativeEnergy(0f));
    }

    [Fact]
    public void testVoiceIsDetectedOnlyAboveTheSilenceThresholdInTheNewAudio()
    {
        // 1.5 s of new audio covers the last 15 values; up to all but the last second of them are checked.
        var energy = Enumerable.Repeat(0f, 30).ToArray();
        Assert.False(StreamingPolicy.IsVoiceDetected(energy, 1.5f));

        energy[0] = 0.9f;                                                   // before the new audio
        Assert.False(StreamingPolicy.IsVoiceDetected(energy, 1.5f));

        energy[20] = StreamingPolicy.SilenceThreshold;                      // at the threshold is silence
        Assert.False(StreamingPolicy.IsVoiceDetected(energy, 1.5f));

        energy[20] = 0.31f;
        Assert.True(StreamingPolicy.IsVoiceDetected(energy, 1.5f));
    }
}

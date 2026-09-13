namespace Spit.Core;

/// One Whisper segment as the streaming loop sees it; times in seconds from the start of the dictation.
public readonly record struct StreamSegment(float Start, float End, string Text);

/// What the streaming loop has settled after a pass, and the tail it has not.
public sealed record StreamConfirmation(
    IReadOnlyList<StreamSegment> Confirmed,
    IReadOnlyList<StreamSegment> Unconfirmed,
    float LastConfirmedSegmentEndSeconds)
{
    public static StreamConfirmation Empty { get; } = new([], [], 0);
}

/// The pure rules of live transcription (prd-spit-mac-windows.md rule 22), taken from WhisperKit's
/// `AudioStreamTranscriber` loop and `AudioProcessor.isVoiceDetected`, which the Mac runs as a
/// dependency, and from mac/Voice/ASR/VoiceAudioProcessor.swift and StreamingTranscriber.swift.
/// Whisper.net has no streaming loop, so Spit.App drives one with these.
public static class StreamingPolicy
{
    /// `AudioStreamTranscriber.requiredSegmentsForConfirmation` (default): the last two segments of a
    /// pass are never confirmed.
    public const int RequiredSegmentsForConfirmation = 2;

    /// A pass runs only when strictly more than this much audio arrived since the last one started
    /// (`nextBufferSeconds > 1` in `transcribeCurrentBuffer`).
    public const float MinimumNewAudioSeconds = 1;

    /// How long the loop sleeps when it decides not to run a pass (`Task.sleep(nanoseconds: 100_000_000)`).
    public const int PollIntervalMs = 100;

    /// `AudioStreamTranscriber.silenceThreshold` (default): relative energy at or below this is silence.
    public const float SilenceThreshold = 0.3f;

    /// `VoiceAudioProcessor.speechFloor`: the RMS that maps to full scale. 1/30 puts the VAD's cutoff
    /// (0.3 × 1/30) at 0.01 — exactly `EnergyGate.DefaultThreshold` — so the stream and the rest of the
    /// app agree on what speech is. The Mac's first value, 0.05, silently dropped quiet sentence endings.
    public const float SpeechFloor = 1f / 30f;

    /// `isVoiceDetected` assumes each relative-energy value covers one 100 ms capture buffer.
    public const int EnergyValueMs = 100;

    /// Speech loudness on the 0–1 scale the VAD expects: min(1, RMS × 30). Raw RMS (0.02–0.15 for
    /// speech) read as silence on the Mac for hours, so every "streamed" dictation fell back unseen.
    public static float RelativeEnergy(float rms) => Math.Min(1, rms / SpeechFloor);

    public static float NewAudioSeconds(int bufferedSamples, int lastBufferSize, int sampleRate = EnergyGate.DefaultSampleRate) =>
        (float)(bufferedSamples - lastBufferSize) / sampleRate;

    /// Whether the loop runs a pass now, or sleeps `PollIntervalMs` and asks again. A skipped buffer is
    /// deferred, not dropped: `lastBufferSize` only advances when a pass runs.
    public static bool ShouldRunPass(int bufferedSamples, int lastBufferSize, IReadOnlyList<float> relativeEnergy, int sampleRate = EnergyGate.DefaultSampleRate)
    {
        var seconds = NewAudioSeconds(bufferedSamples, lastBufferSize, sampleRate);
        return seconds > MinimumNewAudioSeconds && IsVoiceDetected(relativeEnergy, seconds);
    }

    /// Port of WhisperKit's `AudioProcessor.isVoiceDetected(in:nextBufferInSeconds:silenceThreshold:)`:
    /// looks at the energy values covering the new audio, up to all but its last second, and reports
    /// voice when any exceeds the threshold.
    public static bool IsVoiceDetected(IReadOnlyList<float> relativeEnergy, float nextBufferInSeconds, float silenceThreshold = SilenceThreshold)
    {
        var toConsider = Math.Max(0, (int)(nextBufferInSeconds / 0.1f));
        var first = Math.Max(0, relativeEnergy.Count - toConsider);
        var available = relativeEnergy.Count - first;
        var toCheck = Math.Min(available, Math.Max(10, available - 10));
        for (var i = first; i < first + toCheck; i++)
        {
            if (relativeEnergy[i] > silenceThreshold) return true;
        }
        return false;
    }

    /// Folds one pass's segments into the running state, as `transcribeCurrentBuffer` does: all but the
    /// last `RequiredSegmentsForConfirmation` are confirmed, but only when they reach past what was
    /// already confirmed; the rest become the unconfirmed tail. The next pass starts at
    /// `LastConfirmedSegmentEndSeconds` (WhisperKit's `clipTimestamps`).
    public static StreamConfirmation Confirm(StreamConfirmation state, IReadOnlyList<StreamSegment> segments)
    {
        if (segments.Count <= RequiredSegmentsForConfirmation)
            return state with { Unconfirmed = [.. segments] };

        var toConfirm = segments.Take(segments.Count - RequiredSegmentsForConfirmation).ToArray();
        var remaining = segments.Skip(segments.Count - RequiredSegmentsForConfirmation).ToArray();
        var confirmed = state.Confirmed;
        var lastEnd = state.LastConfirmedSegmentEndSeconds;
        if (toConfirm[^1].End > lastEnd)
        {
            lastEnd = toConfirm[^1].End;
            if (!ContainsRun(confirmed, toConfirm)) confirmed = [.. confirmed, .. toConfirm];
        }
        return new StreamConfirmation(confirmed, remaining, lastEnd);
    }

    /// Segment texts as the bar shows them: concatenated (Whisper carries its own leading spaces) and trimmed.
    public static string JoinSegments(IEnumerable<StreamSegment> segments) =>
        string.Concat(segments.Select(s => s.Text)).Trim();

    /// The text to paste when the stream stops, or null to fall back to a one-pass transcription.
    ///
    /// Confirmed *and* unconfirmed: the last two segments are permanently unconfirmed because no more
    /// audio is coming to settle them. Returning confirmed only truncated the end of every streamed
    /// dictation on the Mac, while the bar showed the whole sentence.
    public static string? FinalText(IReadOnlyList<StreamSegment> confirmed, IReadOnlyList<StreamSegment> unconfirmed)
    {
        var text = string.Join(' ', new[] { JoinSegments(confirmed), JoinSegments(unconfirmed) }.Where(p => p.Length > 0)).Trim();
        return text.Length == 0 ? null : text;
    }

    /// How many segments the transcript was assembled from; the skip gate counts unconfirmed ones too.
    public static int SegmentCount(IReadOnlyList<StreamSegment> confirmed, IReadOnlyList<StreamSegment> unconfirmed) =>
        confirmed.Count + unconfirmed.Count;

    /// How far the segments reach, in seconds, never moving backwards (`StreamingTranscriber.coveredSeconds`).
    public static double CoveredSeconds(double previous, IReadOnlyList<StreamSegment> confirmed, IReadOnlyList<StreamSegment> unconfirmed)
    {
        float? lastEnd = unconfirmed.Count > 0 ? unconfirmed[^1].End : confirmed.Count > 0 ? confirmed[^1].End : null;
        return lastEnd is { } end ? Math.Max(previous, end) : previous;
    }

    /// Swift's `Collection.contains(_ other:)`: whether `run` appears contiguously in `items`.
    private static bool ContainsRun(IReadOnlyList<StreamSegment> items, StreamSegment[] run)
    {
        for (var start = 0; start + run.Length <= items.Count; start++)
        {
            var match = true;
            for (var j = 0; j < run.Length && match; j++) match = items[start + j] == run[j];
            if (match) return true;
        }
        return false;
    }
}

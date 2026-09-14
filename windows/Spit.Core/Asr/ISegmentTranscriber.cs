namespace Spit.Core;

/// A transcriber that also hands back Whisper's segments with their times. The live loop needs them to
/// decide what is settled — the Mac streams with `withoutTimestamps: false` for the same reason
/// (mac/Voice/ASR/StreamingTranscriber.swift).
public interface ISegmentTranscriber : ITranscriber
{
    /// Segment `Start`/`End` are seconds from the start of `samples`, not of the dictation; the caller
    /// shifts them. Implementations share one lock with `TranscribeAsync`, so a call here and a tail pass
    /// never run on the model at the same time (rule 42).
    Task<IReadOnlyList<StreamSegment>> TranscribeSegmentsAsync(float[] samples, TranscribeHint hint, CancellationToken ct);
}

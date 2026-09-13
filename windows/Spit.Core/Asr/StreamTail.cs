namespace Spit.Core;

/// Port of `Coordinator.tail(of:afterMs:totalMs:)`, `minimumTailMs` and `overlapMs` in
/// mac/Voice/App/Coordinator.swift.
public static class StreamTail
{
    /// Below this, a gap is a pause or a rounding error rather than a word (`Coordinator.minimumTailMs`).
    public const int MinimumTailMs = 400;

    /// How far before the boundary the final pass starts (`Coordinator.overlapMs`).
    ///
    /// Butt-joining at the boundary loses whichever word straddles it: it is cut in half in the tail's
    /// audio, Whisper drops it, and `Stitch` trusts the tail at the seam — reported as "it cut the word
    /// 'and'". Overlapping keeps the word whole and gives `Stitch` real overlapping text to find the seam in.
    public const int OverlapMs = 1500;

    /// The audio after `afterMs` that the stream never transcribed, or null when there is nothing there
    /// worth a pass.
    ///
    /// Two guards: a gap under `MinimumTailMs` is not a word and invites a hallucinated one, and a gap
    /// that is silence — stopping talking a beat before letting go of the key — is exactly what Whisper
    /// hallucinates on, so `EnergyGate` decides that too. The offset comes from the sample count rather
    /// than a hard-coded rate, so it cannot drift from what the recorder produces.
    public static float[]? Tail(float[] samples, int afterMs, int totalMs)
    {
        if (totalMs <= 0 || samples.Length == 0 || afterMs < 0) return null;
        // Decided on the real gap, not the padded one: the overlap must never make a 100 ms gap look
        // worth transcribing.
        if (totalMs - afterMs < MinimumTailMs) return null;

        int Index(int ms) => (int)((double)ms / totalMs * samples.Length);

        // Speech is judged on the new audio alone. The overlap reaches back into speech already
        // transcribed, so asking about the padded slice would make every silent gap look like speech.
        var boundary = Index(afterMs);
        if (boundary < 0 || boundary >= samples.Length) return null;
        if (!EnergyGate.HasSpeech(samples.AsSpan(boundary))) return null;

        // What to transcribe starts earlier, so the word straddling the boundary is whole.
        return samples[Index(Math.Max(0, afterMs - OverlapMs))..];
    }

    /// The dictation's text from the streamed transcript and a tail pass over `Tail(samples, coveredMs, …)`, or null
    /// when only a transcription of the whole recording can be trusted.
    ///
    /// - A blank tail keeps the streamed text.
    /// - When the stream covered no more than `OverlapMs`, the tail pass started at the first sample: it is the whole
    ///   recording, so it replaces the stream.
    /// - When `Stitch` finds the seam, the stitched text.
    /// - Otherwise null. Appending, the Mac's rule, pastes whatever the tail's overlap heard again — "Hi Joel Hi Joel,
    ///   quick update…" in a CI smoke run, "Hi Joel, quick Joel, quick update…" in review — and text cannot tell that
    ///   apart from a tail of genuinely new speech, so the caller transcribes the whole recording instead. That costs
    ///   the wait live transcription saves, but only when no seam exists; a stream that covered the recording never
    ///   gets here, because `Tail` returns null for it.
    public static string? Combine(string streamed, int coveredMs, string tail)
    {
        if (string.IsNullOrWhiteSpace(tail)) return streamed.Trim();
        if (coveredMs <= OverlapMs) return tail.Trim();
        return Stitch.TryJoin(streamed, tail, out var joined) ? joined : null;
    }
}

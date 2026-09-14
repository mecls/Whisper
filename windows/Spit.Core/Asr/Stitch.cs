namespace Spit.Core;

/// Port of mac/Voice/ASR/Stitch.swift.
///
/// Joins a streamed transcript to a final pass over the audio the stream did not reach.
///
/// The two overlap by an unknown amount, and the segment timestamps cannot say by how much: Whisper's
/// text runs past the timestamp (the tail is transcribed twice — counting to twenty produced
/// `1 … 29, 12 … 20`) or stops short of it (words in between are lost). So the seam is found in the
/// text. The tail is a fresh transcription of real audio and is trusted; where its opening words
/// reappear in the streamed transcript, that is the seam, and everything the stream claimed after it
/// is discarded.
public static class Stitch
{
    /// Words compared for matching: case and punctuation carry no information about where the seam is,
    /// and `"20."` must match `"20"`.
    private static string Key(string word) =>
        new(word.ToLowerInvariant().Where(c => char.IsLetter(c) || char.IsNumber(c)).ToArray());

    /// Anchor length. Long enough that a match means something, short enough to still find the seam
    /// when the tail is a few words. Capped by the tail's own length.
    private const int AnchorWords = 5;
    /// Below this a match is coincidence, and a false seam deletes every word between it and the real one.
    internal const int MinimumAnchor = 3;

    /// The Mac's rule: the stitched text, or the tail appended when no seam is found.
    public static string Join(string streamed, string tail)
    {
        TryJoin(streamed, tail, out var joined);
        return joined;
    }

    /// `Join`, reporting whether a seam was found (or one side was empty). False means `joined` is the tail simply
    /// appended — right when the tail is new speech, a duplication of the overlap when it is not. Text alone cannot
    /// tell those apart, which is why the Windows client re-transcribes instead of appending (`StreamTail.Combine`).
    public static bool TryJoin(string streamed, string tail, out string joined) =>
        TryJoin(streamed, tail, maxTailSkip: 0, out joined);

    /// Leading tail words a Windows join may pass over before its anchor. The overlap cut can leave a fragment ("board"
    /// of "dashboard") or a word the two passes heard differently, and an anchor that must start at the tail's first
    /// word then finds no seam in ordinary speech (fifth review). The skipped words lie inside the overlap, so the
    /// stream already has them.
    internal const int TailSkip = 2;

    /// `TryJoin` allowing the anchor to start at tail word 0, 1 or 2 — longest anchor at the earliest start first.
    public static bool TryJoinAllowingTailSkip(string streamed, string tail, out string joined) =>
        TryJoin(streamed, tail, TailSkip, out joined);

    private static bool TryJoin(string streamed, string tail, int maxTailSkip, out string joined)
    {
        var s = streamed.Trim();
        var t = tail.Trim();
        if (t.Length == 0)
        {
            joined = s;
            return true;
        }
        if (s.Length == 0)
        {
            joined = t;
            return true;
        }

        var sWords = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tWords = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sKeys = sWords.Select(Key).ToArray();

        for (var skip = 0; skip <= maxTailSkip; skip++)
        {
            // Capped by both sides: capping only by the tail misses the seam whenever the tail is the
            // longer of the two (the stream produced almost nothing but a hallucination).
            var longest = Math.Min(AnchorWords, Math.Min(tWords.Length - skip, sWords.Length));

            // Longest anchor first; shorter anchors are a fallback, because the tail's opening words can
            // run past the seam.
            for (var length = longest; length >= MinimumAnchor; length--)
            {
                var anchor = tWords.Skip(skip).Take(length).Select(Key).ToArray();
                // The last occurrence: a phrase repeated earlier in the dictation is not the seam.
                for (var start = sKeys.Length - length; start >= 0; start--)
                {
                    if (sKeys.AsSpan(start, length).SequenceEqual(anchor))
                    {
                        var kept = string.Join(' ', sWords.Take(start));
                        var rest = string.Join(' ', tWords.Skip(skip));
                        joined = kept.Length == 0 ? rest : kept + " " + rest;
                        return true;
                    }
                }
            }
        }

        // No overlap found: on the Mac the tail really is new speech, which is what this pass exists for.
        joined = s + " " + t;
        return false;
    }
}

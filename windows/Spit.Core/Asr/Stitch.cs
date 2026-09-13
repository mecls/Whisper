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
    private const int MinimumAnchor = 3;

    public static string Join(string streamed, string tail)
    {
        var s = streamed.Trim();
        var t = tail.Trim();
        if (t.Length == 0) return s;
        if (s.Length == 0) return t;

        var sWords = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tWords = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var sKeys = sWords.Select(Key).ToArray();
        // Capped by both sides: capping only by the tail misses the seam whenever the tail is the
        // longer of the two (the stream produced almost nothing but a hallucination).
        var longest = Math.Min(AnchorWords, Math.Min(tWords.Length, sWords.Length));
        if (longest < MinimumAnchor) return s + " " + t;

        // Longest anchor first; shorter anchors are a fallback, because the tail's opening words can
        // run past the seam.
        for (var length = longest; length >= MinimumAnchor; length--)
        {
            var anchor = tWords.Take(length).Select(Key).ToArray();
            // The last occurrence: a phrase repeated earlier in the dictation is not the seam.
            for (var start = sKeys.Length - length; start >= 0; start--)
            {
                if (sKeys.AsSpan(start, length).SequenceEqual(anchor))
                {
                    var kept = string.Join(' ', sWords.Take(start));
                    return kept.Length == 0 ? t : kept + " " + t;
                }
            }
        }

        // No overlap found: the tail really is new speech, which is what this pass exists for.
        return s + " " + t;
    }
}

using System.Buffers;
using System.Globalization;
using System.Text;

namespace Spit.Core;

/// Decides whether a transcript needs a cleanup engine at all (prd-sub-second-dictation.md rules
/// 11-13). Port of mac/Voice/Refine/SkipGate.swift.
///
/// Whisper already returns punctuated, capitalized text, so on a short well-formed utterance cleanup
/// has nothing left to do but cost a ~700 ms round trip. The gate decides only *whether* to clean; it
/// never edits text (rule 13). It is deliberately asymmetric: every clause has to pass before it will
/// skip, because a false negative costs 700 ms and a false positive pastes a filler word into someone's
/// document.
public static class SkipGate
{
    /// Rule 11: above this, assume the utterance has enough structure to be worth cleaning.
    /// Provisional — tuned from the real skip rate, which is why the outcome shows in the bar.
    public const int MaxWords = 12;

    /// Tokens that are never anything but fillers, in either language.
    private static readonly HashSet<string> AlwaysFiller = ["um", "uh", "uhh", "er", "erm", "hã", "hum", "ahm"];

    /// Fillers *in context* but ordinary words otherwise: `é` is the verb "is", `pronto` means "ready",
    /// `tipo` "kind", `like` a normal English verb. Matched bare, essentially no Portuguese sentence would
    /// ever skip. They count only when elongated (`ééé`) or comma-adjacent (`tipo,`), which is how they
    /// actually show up in speech.
    private static readonly HashSet<string> ContextualFiller = ["é", "eh", "ah", "tipo", "like", "pronto", "epá", "bem"];

    private static readonly string[] MultiWordFiller = ["you know", "i mean", "sort of", "ou seja assim"];

    /// Spoken formatting the cleanup engine has to turn into real line breaks — the one thing the gate
    /// can never do itself, since it is forbidden from editing text.
    private static readonly string[] Commands = ["new line", "nova linha", "new paragraph", "novo parágrafo", "novo paragrafo"];

    private static readonly HashSet<char> TerminalPunctuation = ['.', '?', '!', '…'];

    /// Why this transcript is going to a cleanup engine. Returned rather than a bare bool so the reason
    /// can be logged while the thresholds are tuned.
    public enum Reason
    {
        NotCleanMode,
        TooLong,
        Filler,
        RepeatedWord,
        SpokenCommand,
        Unterminated,

        /// Assembled from more than one streamed segment, so it carries at least one chunk boundary.
        /// Skipping is the only path where nothing inspects the text before it reaches the document,
        /// and cleanup repairs exactly that class of damage.
        StreamedMultiSegment,
    }

    /// The Mac's `Reason.rawValue`, for logs.
    public static string RawValue(this Reason reason) => reason switch
    {
        Reason.NotCleanMode => "not-clean-mode",
        Reason.TooLong => "too-long",
        Reason.Filler => "filler",
        Reason.RepeatedWord => "repeated-word",
        Reason.SpokenCommand => "spoken-command",
        Reason.Unterminated => "unterminated",
        Reason.StreamedMultiSegment => "streamed-multi-segment",
        _ => reason.ToString(),
    };

    /// <param name="language">The ASR's detected language. When null or unrecognised both filler lists
    /// apply (rule 12) — an unknown language must not become a reason to skip.</param>
    /// <param name="segments">How many confirmed streaming segments produced this text; 1 for a one-pass
    /// transcription, which has no boundaries at all.</param>
    /// <returns>The reason to clean, or null when the text can be pasted as-is.</returns>
    public static Reason? ReasonToClean(string raw, string mode, string? language, int segments = 1)
    {
        if (mode != "clean") return Reason.NotCleanMode;   // rule 13: literal never reaches here
        if (segments > 1) return Reason.StreamedMultiSegment;

        // NFC so a decomposed "é" compares equal to the lists, as Swift's canonical string equality does.
        var text = raw.Trim().Normalize(NormalizationForm.FormC);
        if (text.Length == 0 || !TerminalPunctuation.Contains(text[^1])) return Reason.Unterminated;

        // Invariant rather than pt-PT: Portuguese has no culture-specific casing, and invariant cannot
        // differ between a Mac, a Linux runner and a PC with a Turkish locale.
        var lower = text.ToLowerInvariant();
        foreach (var c in Commands)
            if (lower.Contains(c, StringComparison.Ordinal)) return Reason.SpokenCommand;
        foreach (var f in MultiWordFiller)
            if (lower.Contains(f, StringComparison.Ordinal)) return Reason.Filler;

        // Words for counting and matching: punctuation stripped, but comma adjacency is read first,
        // since that is what disambiguates the contextual fillers.
        var rawTokens = lower.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (rawTokens.Length > MaxWords) return Reason.TooLong;

        var words = new List<string>();
        foreach (var t in rawTokens)
        {
            var commaAdjacent = t.EndsWith(',') || t.EndsWith(';') || t.EndsWith(':');
            var bare = TrimNonAlphanumerics(t);
            if (bare.Length == 0) continue;
            if (AlwaysFiller.Contains(bare)) return Reason.Filler;
            // A held vowel ("ééé", "ehhh") is the same token as its base, drawn out. Collapsing it lets one
            // entry in ContextualFiller cover every length a speaker might hold.
            var elements = TextElements(bare);
            var first = elements[0];
            var heldVowel = elements.Count > 1 && elements.All(e => e == first);
            var key = heldVowel ? first : bare;
            if (ContextualFiller.Contains(key) && (commaAdjacent || heldVowel)) return Reason.Filler;
            words.Add(bare);
        }

        // An immediately repeated word is a false start ("we need to, we need to"), which Whisper
        // transcribes faithfully and only a cleanup pass removes.
        for (var i = 0; i + 1 < words.Count; i++)
            if (words[i] == words[i + 1] && TextElements(words[i]).Count > 1) return Reason.RepeatedWord;

        return null;
    }

    public static bool ShouldSkipCleanup(string raw, string mode, string? language, int segments = 1) =>
        ReasonToClean(raw, mode, language, segments) is null;

    /// Swift's `trimmingCharacters(in: .alphanumerics.inverted)`: strips scalars outside L*, M* and N*
    /// from both ends.
    private static string TrimNonAlphanumerics(string s)
    {
        int start = 0, end = s.Length;
        while (start < end)
        {
            Rune.DecodeFromUtf16(s.AsSpan(start, end - start), out var rune, out var consumed);
            if (IsAlphanumeric(rune)) break;
            start += consumed;
        }
        while (end > start)
        {
            if (Rune.DecodeLastFromUtf16(s.AsSpan(start, end - start), out var rune, out var consumed) == OperationStatus.Done
                && IsAlphanumeric(rune)) break;
            end -= consumed;
        }
        return s[start..end];
    }

    private static bool IsAlphanumeric(Rune r) => Rune.GetUnicodeCategory(r) <= UnicodeCategory.OtherNumber;

    /// Grapheme clusters, which is what Swift's `Character` counts.
    private static List<string> TextElements(string s)
    {
        var result = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) result.Add(e.GetTextElement());
        return result;
    }
}

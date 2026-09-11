import Foundation

/// Which engine produced the text that got pasted. Recorded as `llmModel` so the three
/// populations can be separated when the acceptance percentiles are read back
/// (prd-sub-second-dictation.md rule 17) — an aggregate latency number over a mixture of
/// "cleaned by gemma4", "cleaned on device" and "not cleaned at all" means nothing.
enum CleanupEngine {
    static let skipped = "skipped"
}

/**
 Decides whether a transcript needs a cleanup engine at all (rules 11-13).

 WhisperKit already returns punctuated, capitalized text, so on a short well-formed utterance the
 cleanup engine has nothing left to do but cost a network round trip — ~700 ms on the measured
 gemma4 path. Skipping those is the only change in this spec that removes a round trip from a
 dictation entirely rather than making it faster.

 The gate decides only *whether* to clean; it never edits text (rule 13). It is deliberately
 asymmetric: every clause has to pass before it will skip, because a false negative costs 700 ms
 and a false positive pastes a filler word into someone's document.
 */
enum SkipGate {
    /// Rule 11: above this, assume the utterance has enough structure to be worth cleaning.
    /// Provisional — §7 open question 3 settles it from the real skip rate once this has run for a
    /// week, which is why rule 24 puts the outcome in the HUD.
    static let maxWords = 12

    /// Tokens that are never anything but fillers, in either language.
    private static let alwaysFiller: Set<String> = ["um", "uh", "uhh", "er", "erm", "hã", "hum", "ahm"]

    /// Tokens that are fillers *in context* but ordinary words otherwise: `é` is the Portuguese
    /// verb "is", `pronto` means "ready", `tipo` means "kind/type", `like` is a normal English
    /// verb. Matching these bare would mean essentially no Portuguese sentence ever skips, which
    /// would hand half the users none of the benefit. They count as fillers only when elongated
    /// (`ééé`) or comma-adjacent (`tipo,`), which is how they actually show up in speech — see the
    /// pt/en fixtures in `server/src/cli/bench-fixtures.ts`.
    private static let contextualFiller: Set<String> = ["é", "eh", "ah", "tipo", "like", "pronto", "epá", "bem"]

    private static let multiWordFiller = ["you know", "i mean", "sort of", "ou seja assim"]

    /// Spoken formatting the cleanup engine has to turn into real line breaks — the one thing the
    /// gate can never do itself, since it is forbidden from editing text.
    private static let commands = ["new line", "nova linha", "new paragraph", "novo parágrafo", "novo paragrafo"]

    private static let terminalPunctuation: Set<Character> = [".", "?", "!", "…"]

    /// Why this transcript is going to a cleanup engine, or nil when it can be pasted as-is.
    /// Returned rather than a bare Bool so the reason can be logged while the thresholds are tuned.
    enum Reason: String {
        case notCleanMode = "not-clean-mode"
        case tooLong = "too-long"
        case filler
        case repeatedWord = "repeated-word"
        case spokenCommand = "spoken-command"
        case unterminated
    }

    /// - Parameter language: the ASR's detected language. When nil or unrecognised, both filler
    ///   lists apply (rule 12) — an unknown language must not become a reason to skip.
    static func reasonToClean(raw: String, mode: String, language: String?) -> Reason? {
        guard mode == "clean" else { return .notCleanMode }   // rule 13: literal never reaches here

        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let last = text.last, terminalPunctuation.contains(last) else { return .unterminated }

        let lower = text.lowercased(with: Locale(identifier: "pt_PT"))
        for c in commands where lower.contains(c) { return .spokenCommand }
        for f in multiWordFiller where lower.contains(f) { return .filler }

        // Words for counting and matching: punctuation stripped, but we keep the comma-adjacency
        // information first, since that is what disambiguates the contextual fillers.
        let rawTokens = lower.split(whereSeparator: { $0 == " " || $0 == "\n" }).map(String.init)
        guard rawTokens.count <= maxWords else { return .tooLong }

        var words: [String] = []
        for t in rawTokens {
            let commaAdjacent = t.hasSuffix(",") || t.hasSuffix(";") || t.hasSuffix(":")
            let bare = t.trimmingCharacters(in: CharacterSet.alphanumerics.inverted)
            guard let firstChar = bare.first else { continue }
            if alwaysFiller.contains(bare) { return .filler }
            // A held vowel ("ééé", "ehhh") is the same token as its base, drawn out. Collapsing it
            // is what lets one entry in `contextualFiller` cover every length a speaker might hold.
            let heldVowel = bare.count > 1 && bare.allSatisfy { $0 == firstChar }
            let key = heldVowel ? String(firstChar) : bare
            if contextualFiller.contains(key), commaAdjacent || heldVowel { return .filler }
            words.append(bare)
        }

        // An immediately repeated word is a false start ("we need to, we need to"), which Whisper
        // transcribes faithfully and only a cleanup pass removes.
        for (a, b) in zip(words, words.dropFirst()) where a == b && a.count > 1 { return .repeatedWord }

        return nil
    }

    static func shouldSkipCleanup(raw: String, mode: String, language: String?) -> Bool {
        reasonToClean(raw: raw, mode: mode, language: language) == nil
    }
}

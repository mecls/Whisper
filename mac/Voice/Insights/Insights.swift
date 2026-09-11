import Foundation

/// The `GET /v1/insights` response, verbatim (docs/API.md).
///
/// Numbers only, by design — the server's handler never selects `raw` or `cleaned`, so there is no
/// transcript text in this type and none can appear in the cache file it gets written to. That is
/// what makes caching the response to disk compatible with the app's promise that transcripts
/// never land on the Mac's disk.
struct Insights: Codable, Equatable {
    struct Totals: Codable, Equatable {
        let dictations: Int
        let words: Int
        let audioMs: Int
        /// `nil` below a minute of recorded audio: a rate computed from three seconds of speech is
        /// arithmetically true and means nothing, so the card shows a dash instead.
        let wpm: Int?
    }

    struct Streak: Codable, Equatable {
        let current: Int
        let longest: Int
    }

    struct Day: Codable, Equatable, Identifiable {
        let date: String        // YYYY-MM-DD in the requested zone
        let dictations: Int
        let words: Int
        var id: String { date }
    }

    struct AppShare: Codable, Equatable, Identifiable {
        let label: String
        let dictations: Int
        let share: Int          // integer percent; the whole list sums to exactly 100
        var id: String { label }
    }

    let totals: Totals
    let streak: Streak
    /// Every day in the window, zero-filled — the server never omits a quiet day, so the grid below
    /// never has to infer one.
    let days: [Day]
    let apps: [AppShare]
    /// Server clock, ms since epoch. Used for the staleness line, which is why it is the server's
    /// time and not the time the file was written: it says how old the *numbers* are.
    let generatedAt: Int
}

extension Insights {
    var isEmpty: Bool { totals.dictations == 0 }
}

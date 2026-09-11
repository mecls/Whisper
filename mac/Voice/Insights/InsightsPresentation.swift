import Foundation

/// What the window should draw, decided outside any `View` body so it can be tested.
///
/// The four states in the spec are not independent — they are the product of "do we have numbers"
/// and "did the last refresh fail". Deriving them in one place is what keeps the window from ever
/// showing a spinner that never resolves, or an error dialog, both of which are explicitly ruled
/// out (rules 21 and 23).
struct InsightsPresentation: Equatable {
    enum Content: Equatable {
        /// No numbers yet and a request in flight. Only ever visible on a genuine first run —
        /// with a cache present the window goes straight to `.empty` or `.populated`.
        case loading
        /// Zero dictations. Not hypothetical: it is what the first person to open this window
        /// sees, so every card renders its frame with a dash rather than a spinner.
        case empty
        case populated(Insights)
    }

    enum Notice: Equatable {
        case none
        /// The refresh failed. Carries `generatedAt` from whatever numbers are on screen, so the
        /// line can say how old they are; nil when there is nothing cached to be stale.
        case unreachable(generatedAt: Int?)
        /// A 401. Different copy, because "couldn't reach the server" would send the user to
        /// check their Wi-Fi when the fix is in Settings › Server.
        case unauthorized
    }

    let content: Content
    let notice: Notice

    static func make(insights: Insights?, isRefreshing: Bool, failure: APIError?) -> InsightsPresentation {
        let content: Content
        if let insights {
            content = insights.isEmpty ? .empty : .populated(insights)
        } else if isRefreshing {
            content = .loading
        } else {
            // No cache and nothing in flight — a failed first refresh. The empty state is the
            // honest thing to draw; the notice below says why it is empty.
            content = .empty
        }

        let notice: Notice
        switch failure {
        case .none: notice = .none
        case .unauthorized: notice = .unauthorized
        default: notice = .unreachable(generatedAt: insights?.generatedAt)
        }
        return InsightsPresentation(content: content, notice: notice)
    }
}

enum InsightsFormat {
    /// Thousands-separated in the user's locale, so 64860 reads as 64,860 rather than a wall of
    /// digits the eye has to count.
    static func number(_ n: Int) -> String {
        let f = NumberFormatter()
        f.numberStyle = .decimal
        return f.string(from: NSNumber(value: n)) ?? String(n)
    }

    /// "2 minutes ago", "yesterday". Takes the server's `generatedAt` (ms epoch), because the
    /// staleness line is about how old the numbers are, not how old the file is.
    static func relativeTime(sinceMsEpoch ms: Int, now: Date = Date()) -> String {
        let date = Date(timeIntervalSince1970: Double(ms) / 1000)
        let f = RelativeDateTimeFormatter()
        f.unitsStyle = .full
        return f.localizedString(for: date, relativeTo: now)
    }
}

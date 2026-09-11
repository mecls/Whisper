import Foundation
import os

private let log = Logger(subsystem: "co.miraside.voice", category: "insights-cache")

/// The last successful `GET /v1/insights` response, on disk.
///
/// Its whole job is that the window has something to draw the instant it opens, including with the
/// Wi-Fi off. Writing it is only safe because the response contains no transcript text (see
/// `Insights`); if that ever stops being true, this file has to go, not gain a filter.
struct InsightsCache: Sendable {
    let url: URL

    static let `default` = InsightsCache(url: Self.defaultURL())

    private static func defaultURL() -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return base.appendingPathComponent("Voice/insights-cache.json")
    }

    /// A missing, unreadable or corrupt file is "no cache", never an error the user sees. The
    /// window then shows its loading state and refreshes, which is the same thing it does on a
    /// first run — one refresh is the entire cost of a bad file.
    func load() -> Insights? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        do {
            return try JSONDecoder().decode(Insights.self, from: data)
        } catch {
            log.info("discarding unreadable insights cache: \(String(describing: error), privacy: .public)")
            return nil
        }
    }

    func save(_ insights: Insights) {
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try JSONEncoder().encode(insights).write(to: url, options: .atomic)
        } catch {
            // A cache that cannot be written costs one refresh next time. It is never worth
            // failing, or telling the user about, an operation they did not ask for.
            log.error("could not write insights cache: \(error.localizedDescription, privacy: .public)")
        }
    }
}

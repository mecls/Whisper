import Foundation

/// Drives the Insights window: cache first, then one refresh.
///
/// There is deliberately no retry loop, no backoff timer and no background polling. A failed
/// refresh leaves the cached numbers exactly where they are and tries again the next time the
/// window opens (rule 23) — a dashboard that silently retries is a dashboard that hammers the VPS
/// while sitting open on a second monitor all day.
@MainActor
final class InsightsModel: ObservableObject {
    /// 21 weeks plus the current one is what fits the card at its default width without scrolling.
    static let weeks = 21

    @Published private(set) var presentation: InsightsPresentation

    private let api: VoiceAPIClient
    private let cache: InsightsCache
    private var insights: Insights?
    private var isRefreshing = false
    private var failure: APIError?
    private var inFlight: Task<Void, Never>?

    init(api: VoiceAPIClient, cache: InsightsCache = .default) {
        self.api = api
        self.cache = cache
        // Read before the first render, so the window opens with numbers already on it rather than
        // flashing a loading state it will leave 200 ms later.
        let cached = cache.load()
        insights = cached
        presentation = .make(insights: cached, isRefreshing: false, failure: nil)
    }

    /// The zone the user's calendar days are in. Read at refresh time, not at init: somebody who
    /// changes zone mid-flight should get their days re-bucketed on the next refresh.
    var timeZoneIdentifier: String { TimeZone.current.identifier }

    func refresh() {
        // A second refresh while one is in flight would only race the first to write the cache.
        guard inFlight == nil else { return }
        isRefreshing = true
        failure = nil
        publish()

        inFlight = Task { [weak self] in
            guard let self else { return }
            do {
                let fresh = try await api.insights(tz: timeZoneIdentifier, weeks: Self.weeks)
                insights = fresh
                failure = nil
                let cache = self.cache
                // Off the main actor: the window is on screen and a disk write has no business
                // being in front of it, however small.
                Task.detached(priority: .utility) { cache.save(fresh) }
            } catch let error as APIError {
                failure = error
            } catch {
                failure = .server(-1)
            }
            isRefreshing = false
            inFlight = nil
            publish()
        }
    }

    private func publish() {
        presentation = .make(insights: insights, isRefreshing: isRefreshing, failure: failure)
    }
}

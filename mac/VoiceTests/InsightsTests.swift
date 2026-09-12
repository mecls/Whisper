import XCTest
@testable import Voice

/// prd-insights-dashboard.md rules 18-23 on the client side, plus build spec AC-7.
///
/// The window itself is not tested — SwiftUI views are not where the mistakes are. The mistakes
/// are in which state gets chosen and which cell a day lands in, and both of those live in plain
/// value types precisely so they can be checked here.
final class InsightsTests: XCTestCase {

    // MARK: - Fixtures

    private func day(_ date: String, _ n: Int, words: Int = 0) -> Insights.Day {
        Insights.Day(date: date, dictations: n, words: words)
    }

    private func insights(
        dictations: Int = 10, words: Int = 100, wpm: Int? = 150,
        current: Int = 3, longest: Int = 9,
        days: [Insights.Day] = [], apps: [Insights.AppShare] = [],
        generatedAt: Int = 1_757_400_000_000,
    ) -> Insights {
        Insights(
            totals: .init(dictations: dictations, words: words, audioMs: 40_000, wpm: wpm),
            streak: .init(current: current, longest: longest),
            days: days, apps: apps, generatedAt: generatedAt,
        )
    }

    // MARK: - Presentation state (rules 21, 23)

    func testCachedNumbersRenderImmediatelyEvenWhileRefreshing() {
        // Rule 22: the loading state must never be visible when there is a cache. Somebody who
        // opens this window twice a day should see numbers both times, instantly.
        let p = InsightsPresentation.make(insights: insights(), isRefreshing: true, failure: nil)
        XCTAssertEqual(p.content, .populated(insights()))
        XCTAssertEqual(p.notice, .none)
    }

    func testLoadingOnlyWhenThereIsNothingCached() {
        let p = InsightsPresentation.make(insights: nil, isRefreshing: true, failure: nil)
        XCTAssertEqual(p.content, .loading)
    }

    func testZeroDictationsIsTheEmptyStateNotAPopulatedZero() {
        // The state that actually ships: the server holds zero dictations today, so this is what
        // the first person to open the window sees. A `0` would read as a measurement.
        let p = InsightsPresentation.make(insights: insights(dictations: 0, words: 0, wpm: nil), isRefreshing: false, failure: nil)
        XCTAssertEqual(p.content, .empty)
    }

    func testAFailedRefreshKeepsTheCachedNumbersAndAddsAStalenessNotice() {
        // AC-7: offline must leave the numbers exactly where they are. Never a dialog, never an
        // empty window.
        let cached = insights(generatedAt: 1_757_400_000_000)
        for error in [APIError.offline, .timeout, .server(500), .decoding] {
            let p = InsightsPresentation.make(insights: cached, isRefreshing: false, failure: error)
            XCTAssertEqual(p.content, .populated(cached), "\(error) must not blank the cards")
            XCTAssertEqual(p.notice, .unreachable(generatedAt: 1_757_400_000_000))
        }
    }

    func testAFailedFirstRefreshShowsTheEmptyStateNotAStuckSpinner() {
        let p = InsightsPresentation.make(insights: nil, isRefreshing: false, failure: .offline)
        XCTAssertEqual(p.content, .empty, "a spinner that never resolves is the one thing rule 21 forbids")
        XCTAssertEqual(p.notice, .unreachable(generatedAt: nil))
    }

    func testAnExpiredTokenSaysSoRatherThanBlamingTheNetwork() {
        // Rule 23: "couldn't reach the server" would send the user to check their Wi-Fi when the
        // fix is in Settings › Server.
        let p = InsightsPresentation.make(insights: insights(), isRefreshing: false, failure: .unauthorized)
        XCTAssertEqual(p.notice, .unauthorized)
        XCTAssertEqual(p.content, .populated(insights()), "an expired token still shows the numbers it already has")
    }

    // MARK: - Heatmap bucketing (rules 18, 20)

    func testEachDayLandsOnItsOwnWeekdayRow() {
        // 2026-09-06 is a Sunday; the week runs to Saturday the 12th.
        let days = (6...12).map { day(String(format: "2026-09-%02d", $0), $0 - 5) }
        let grid = HeatmapGrid.build(days: days)
        XCTAssertEqual(grid.weeks.count, 1)
        let week = grid.weeks[0]
        XCTAssertEqual(week[0]?.date, "2026-09-06", "Sunday is row 0")
        XCTAssertEqual(week[6]?.date, "2026-09-12", "Saturday is row 6")
        XCTAssertEqual(week.compactMap { $0 }.count, 7)
    }

    func testANewColumnStartsOnEverySunday() {
        // Three Sundays -> three columns, however many days each one actually contains.
        let days = ["2026-08-30", "2026-09-01", "2026-09-06", "2026-09-08", "2026-09-13"].map { day($0, 1) }
        let grid = HeatmapGrid.build(days: days)
        XCTAssertEqual(grid.weeks.count, 3)
    }

    func testAPartialFinalWeekIsPaddedRatherThanShifted() {
        // The window ends today, mid-week. The remaining days must stay empty in their own rows,
        // not slide the real days along — a one-row shift is invisible and wrong.
        let days = ["2026-09-06", "2026-09-07", "2026-09-08"].map { day($0, 2) }
        let grid = HeatmapGrid.build(days: days)
        XCTAssertEqual(grid.weeks.count, 1)
        XCTAssertEqual(grid.weeks[0].count, 7, "a column is always seven rows")
        XCTAssertNotNil(grid.weeks[0][0])
        XCTAssertNotNil(grid.weeks[0][2])
        XCTAssertNil(grid.weeks[0][3], "days after the last one are blank, not missing")
    }

    func testAWindowStartingMidWeekStillAlignsToTheRightRows() {
        // Defensive: a cached response from an older server might not begin on a Sunday. Deriving
        // the weekday from the date rather than the index is what stops the whole grid shifting.
        let days = ["2026-09-09", "2026-09-10", "2026-09-11"].map { day($0, 1) }  // Wed, Thu, Fri
        let grid = HeatmapGrid.build(days: days)
        XCTAssertEqual(grid.weeks.count, 1)
        XCTAssertNil(grid.weeks[0][0], "no Sunday in this window")
        XCTAssertEqual(grid.weeks[0][3]?.date, "2026-09-09", "Wednesday is row 3")
    }

    func testIntensityLevelsAreFixedThresholdsNotRelativeToTheBusiestDay() {
        // Rule 18. Relative shading would repaint a quiet week darker and imply activity that
        // never happened.
        XCTAssertEqual(HeatmapGrid.level(forDictations: 0), 0)
        XCTAssertEqual(HeatmapGrid.level(forDictations: 1), 1)
        XCTAssertEqual(HeatmapGrid.level(forDictations: 2), 1)
        XCTAssertEqual(HeatmapGrid.level(forDictations: 3), 2)
        XCTAssertEqual(HeatmapGrid.level(forDictations: 5), 2)
        XCTAssertEqual(HeatmapGrid.level(forDictations: 6), 3)
        XCTAssertEqual(HeatmapGrid.level(forDictations: 400), 3)
    }

    func testZeroFilledDaysStillOccupyTheirCell() {
        // Rule 20: the server sends quiet days as zeroes rather than omitting them, so the grid
        // must place them — dropping them here would reintroduce exactly the gap-inference bug
        // the server rule exists to prevent.
        let days = (6...12).map { day(String(format: "2026-09-%02d", $0), $0 == 8 ? 0 : 1) }
        let grid = HeatmapGrid.build(days: days)
        XCTAssertNotNil(grid.weeks[0][2], "Tuesday exists")
        XCTAssertEqual(grid.weeks[0][2]?.level, 0, "…and is drawn at level 0, not left blank")
    }

    func testAMalformedDateIsSkippedRatherThanCrashing() {
        let grid = HeatmapGrid.build(days: [day("not-a-date", 3), day("2026-09-06", 1)])
        XCTAssertEqual(grid.weeks.count, 1)
        XCTAssertEqual(grid.weeks[0].compactMap { $0 }.count, 1)
    }

    func testTheEmptyGridIsStillDrawn() {
        let grid = HeatmapGrid.empty()
        XCTAssertEqual(grid.weeks.count, 22)
        XCTAssertTrue(grid.isEmpty)
        XCTAssertTrue(grid.weeks.allSatisfy { $0.count == 7 })
    }

    // MARK: - Cache (rule 22, AC-7)

    private func tempCache() -> InsightsCache {
        let dir = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
        // Removed afterwards: every call used to leave a directory behind in the system temp
        // folder, and a run touches this helper a dozen times.
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        return InsightsCache(url: dir.appendingPathComponent("insights-cache.json"))
    }

    func testTheCacheRoundTripsTheResponseVerbatim() {
        let cache = tempCache()
        let original = insights(days: [day("2026-09-06", 2, words: 20)], apps: [.init(label: "Slack", dictations: 4, share: 100)])
        cache.save(original)
        XCTAssertEqual(cache.load(), original)
    }

    func testAMissingCacheIsNilNotAnError() {
        XCTAssertNil(tempCache().load())
    }

    func testACorruptCacheIsTreatedAsAbsent() {
        // Rule: parse failure -> loading state -> refresh. Never a crash, never a parse error on
        // screen. One refresh is the whole cost of a bad file.
        //
        // NOTE: this test is *why* every run of the suite prints
        //
        //     [insights-cache] discarding unreadable insights cache: DecodingError.dataCorrupted …
        //     "Unexpected character 't' around line 1, column 4." NSJSONSerializationErrorIndex=3
        //
        // That line is this test passing — `load()` reporting that it threw a bad file away, which
        // is the behaviour being asserted two lines below. It is not a fault in the app and it says
        // nothing about the real cache in Application Support. It was mistaken for a live bug more
        // than once before anyone matched the bytes: that index-3 't' is the `t` of "this" in the
        // corrupt fixture on the next line, and nothing else in the codebase produces it.
        let cache = tempCache()
        try? FileManager.default.createDirectory(at: cache.url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? Data("{ this is not json".utf8).write(to: cache.url)
        XCTAssertNil(cache.load())
    }

    func testNoTranscriptTextCanReachTheCacheFile() {
        // The client half of AC-4. `Insights` has no field that could hold a transcript, so the
        // file on disk cannot contain one — this asserts that the type, not just the server, is
        // what keeps the app's "transcripts never touch this disk" promise true.
        let cache = tempCache()
        cache.save(insights(days: [day("2026-09-06", 1, words: 12)], apps: [.init(label: "Mail", dictations: 1, share: 100)]))
        let written = String(data: (try? Data(contentsOf: cache.url)) ?? Data(), encoding: .utf8) ?? ""
        XCTAssertFalse(written.contains("\"raw\""))
        XCTAssertFalse(written.contains("\"cleaned\""))
        XCTAssertFalse(written.isEmpty)
    }

    // MARK: - The model

    @MainActor
    func testTheModelRendersTheCacheBeforeAnyRequestIsMade() async {
        let cache = tempCache()
        let cached = insights(words: 4242)
        cache.save(cached)
        let api = StubAPI()
        api.offline = true

        let model = InsightsModel(api: api, cache: cache)
        XCTAssertEqual(model.presentation.content, .populated(cached), "the window must open with numbers on it")
        XCTAssertEqual(api.insightsCalls.count, 0, "…before the network is touched at all")
    }

    @MainActor
    func testARefreshReplacesTheCacheAndClearsTheNotice() async {
        let cache = tempCache()
        let api = StubAPI()
        let fresh = insights(words: 9001)
        api.insightsResult = .success(fresh)

        let model = InsightsModel(api: api, cache: cache)
        model.refresh()
        await waitUntil { model.presentation.content == .populated(fresh) }

        XCTAssertEqual(model.presentation.notice, .none)
        XCTAssertEqual(api.insightsCalls.first?.weeks, InsightsModel.weeks)
        XCTAssertEqual(api.insightsCalls.first?.tz, TimeZone.current.identifier, "the user's own calendar days, not UTC")
        await waitUntil { cache.load() == fresh }
    }

    @MainActor
    func testAFailedRefreshLeavesTheCachedNumbersOnScreen() async {
        // AC-7 end to end on the client: cache present, server unreachable.
        let cache = tempCache()
        let cached = insights(words: 777)
        cache.save(cached)
        let api = StubAPI()
        api.offline = true

        let model = InsightsModel(api: api, cache: cache)
        model.refresh()
        await waitUntil { model.presentation.notice != .none }

        XCTAssertEqual(model.presentation.content, .populated(cached))
        XCTAssertEqual(model.presentation.notice, .unreachable(generatedAt: cached.generatedAt))
        XCTAssertEqual(cache.load(), cached, "a failed refresh must not touch the cache")
    }

    @MainActor
    func testAnUnauthorizedRefreshShowsTheTokenNotice() async {
        let cache = tempCache()
        let api = StubAPI()
        api.insightsResult = .failure(APIError.unauthorized)

        let model = InsightsModel(api: api, cache: cache)
        model.refresh()
        await waitUntil { model.presentation.notice != .none }
        XCTAssertEqual(model.presentation.notice, .unauthorized)
    }

    @MainActor
    func testOverlappingRefreshesCollapseToOneRequest() async {
        // Two refreshes in flight would only race each other to write the cache.
        let api = StubAPI()
        api.insightsResult = .success(insights())
        api.delay = 40_000_000
        let model = InsightsModel(api: api, cache: tempCache())

        model.refresh()
        model.refresh()
        model.refresh()
        await waitUntil { model.presentation.notice == .none && !api.insightsCalls.isEmpty }
        XCTAssertEqual(api.insightsCalls.count, 1)
        // The refresh succeeded, so it started a cache write. Left in flight it outlives this test
        // and recreates the temp directory teardown has already removed.
        //
        // Waiting for the write to *exist* first is the point: the assertion above is satisfied as
        // soon as the request has been made, which is 40 ms before the stub answers and well before
        // `pendingSave` is assigned. Awaiting it there awaits nil and does nothing.
        await waitUntil { model.pendingSave != nil }
        await model.pendingSave?.value
    }

    // MARK: - Formatting

    func testLargeNumbersAreGrouped() {
        XCTAssertNotEqual(InsightsFormat.number(64_860), "64860", "a five-digit number needs a separator to be readable at a glance")
        XCTAssertEqual(InsightsFormat.number(7), "7")
    }

    // MARK: - Helpers

    /// Polls the main actor rather than sleeping a fixed interval, so the test is not a race on a
    /// slow machine and not a needless wait on a fast one.
    @MainActor
    private func waitUntil(timeout: TimeInterval = 3, _ condition: () -> Bool) async {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() && Date() < deadline {
            try? await Task.sleep(nanoseconds: 5_000_000)
        }
        XCTAssertTrue(condition(), "condition never became true within \(timeout)s")
    }
}

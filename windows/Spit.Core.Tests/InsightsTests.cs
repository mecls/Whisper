using System.Globalization;

namespace Spit.Core.Tests;

/// Port of mac/VoiceTests/InsightsTests.swift: prd-insights-dashboard.md rules 18-23 on the client side.
///
/// The view itself is not tested — the mistakes are in which state gets chosen and which cell a day lands
/// in, and both live in plain types precisely so they can be checked here.
public sealed class InsightsTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // MARK: - Fixtures

    private static InsightsDay day(string date, int n, int words = 0) => new(date, n, words);

    private static Insights insights(
        int dictations = 10, int words = 100, int? wpm = 150,
        int current = 3, int longest = 9,
        InsightsDay[]? days = null, InsightsAppShare[]? apps = null,
        long generatedAt = 1_757_400_000_000) =>
        new(new InsightsTotals(dictations, words, 40_000, wpm),
            new InsightsStreak(current, longest),
            days ?? [], apps ?? [], generatedAt);

    private static InsightsContent populated(Insights i) => new InsightsContent.Populated(i);
    private static InsightsNotice none => new InsightsNotice.None();

    // MARK: - Presentation state (rules 21, 23)

    [Fact]
    public void testCachedNumbersRenderImmediatelyEvenWhileRefreshing()
    {
        // Rule 22: the loading state must never be visible when there is a cache.
        var p = InsightsPresentation.Make(insights(), isRefreshing: true, failure: null);
        Assert.Equal(populated(insights()), p.Content);
        Assert.Equal(none, p.Notice);
    }

    [Fact]
    public void testLoadingOnlyWhenThereIsNothingCached()
    {
        var p = InsightsPresentation.Make(null, isRefreshing: true, failure: null);
        Assert.Equal<InsightsContent>(new InsightsContent.Loading(), p.Content);
    }

    [Fact]
    public void testZeroDictationsIsTheEmptyStateNotAPopulatedZero()
    {
        // What the first person to open the window sees. A `0` would read as a measurement.
        var p = InsightsPresentation.Make(insights(dictations: 0, words: 0, wpm: null), isRefreshing: false, failure: null);
        Assert.Equal<InsightsContent>(new InsightsContent.Empty(), p.Content);
    }

    [Fact]
    public void testAFailedRefreshKeepsTheCachedNumbersAndAddsAStalenessNotice()
    {
        // AC-7: offline must leave the numbers exactly where they are. Never a dialog, never an empty window.
        var cached = insights(generatedAt: 1_757_400_000_000);
        foreach (var error in new[] { ApiException.Offline(), ApiException.Timeout(), ApiException.ServerStatus(500), ApiException.Decoding() })
        {
            var p = InsightsPresentation.Make(cached, isRefreshing: false, failure: error);
            Assert.True(p.Content == populated(cached), $"{error.Message} must not blank the cards");
            Assert.Equal<InsightsNotice>(new InsightsNotice.Unreachable(1_757_400_000_000), p.Notice);
        }
    }

    [Fact]
    public void testAFailedFirstRefreshShowsTheEmptyStateNotAStuckSpinner()
    {
        var p = InsightsPresentation.Make(null, isRefreshing: false, failure: ApiException.Offline());
        // A spinner that never resolves is the one thing rule 21 forbids.
        Assert.Equal<InsightsContent>(new InsightsContent.Empty(), p.Content);
        Assert.Equal<InsightsNotice>(new InsightsNotice.Unreachable(null), p.Notice);
    }

    [Fact]
    public void testAnExpiredTokenSaysSoRatherThanBlamingTheNetwork()
    {
        // Rule 23: "couldn't reach the server" would send the user to check their Wi-Fi when the fix is in
        // Settings › Server.
        var p = InsightsPresentation.Make(insights(), isRefreshing: false, failure: ApiException.Unauthorized());
        Assert.Equal<InsightsNotice>(new InsightsNotice.Unauthorized(), p.Notice);
        // An expired token still shows the numbers it already has.
        Assert.Equal(populated(insights()), p.Content);
    }

    // MARK: - Heatmap bucketing (rules 18, 20)

    [Fact]
    public void testEachDayLandsOnItsOwnWeekdayRow()
    {
        // 2026-09-06 is a Sunday; the week runs to Saturday the 12th.
        var days = Enumerable.Range(6, 7).Select(d => day($"2026-09-{d:00}", d - 5));
        var grid = HeatmapGrid.Build(days);
        var week = Assert.Single(grid.Weeks);
        Assert.Equal("2026-09-06", week[0]?.Date);   // Sunday is row 0
        Assert.Equal("2026-09-12", week[6]?.Date);   // Saturday is row 6
        Assert.Equal(7, week.Count(c => c is not null));
    }

    [Fact]
    public void testANewColumnStartsOnEverySunday()
    {
        // Three Sundays -> three columns, however many days each one actually contains.
        var days = new[] { "2026-08-30", "2026-09-01", "2026-09-06", "2026-09-08", "2026-09-13" }.Select(d => day(d, 1));
        var grid = HeatmapGrid.Build(days);
        Assert.Equal(3, grid.Weeks.Count);
    }

    [Fact]
    public void testAPartialFinalWeekIsPaddedRatherThanShifted()
    {
        // The window ends today, mid-week. The remaining days stay empty in their own rows, not slide the
        // real days along — a one-row shift is invisible and wrong.
        var days = new[] { "2026-09-06", "2026-09-07", "2026-09-08" }.Select(d => day(d, 2));
        var grid = HeatmapGrid.Build(days);
        var week = Assert.Single(grid.Weeks);
        Assert.Equal(7, week.Count);   // a column is always seven rows
        Assert.NotNull(week[0]);
        Assert.NotNull(week[2]);
        Assert.Null(week[3]);          // days after the last one are blank, not missing
    }

    [Fact]
    public void testAWindowStartingMidWeekStillAlignsToTheRightRows()
    {
        // Defensive: a cached response from an older server might not begin on a Sunday. Deriving the
        // weekday from the date rather than the index is what stops the whole grid shifting.
        var days = new[] { "2026-09-09", "2026-09-10", "2026-09-11" }.Select(d => day(d, 1));   // Wed, Thu, Fri
        var grid = HeatmapGrid.Build(days);
        var week = Assert.Single(grid.Weeks);
        Assert.Null(week[0]);                         // no Sunday in this window
        Assert.Equal("2026-09-09", week[3]?.Date);    // Wednesday is row 3
    }

    [Fact]
    public void testIntensityLevelsAreFixedThresholdsNotRelativeToTheBusiestDay()
    {
        // Rule 18. Relative shading would repaint a quiet week darker and imply activity that never happened.
        Assert.Equal(0, HeatmapGrid.Level(0));
        Assert.Equal(1, HeatmapGrid.Level(1));
        Assert.Equal(1, HeatmapGrid.Level(2));
        Assert.Equal(2, HeatmapGrid.Level(3));
        Assert.Equal(2, HeatmapGrid.Level(5));
        Assert.Equal(3, HeatmapGrid.Level(6));
        Assert.Equal(3, HeatmapGrid.Level(400));
    }

    [Fact]
    public void testZeroFilledDaysStillOccupyTheirCell()
    {
        // Rule 20: the server sends quiet days as zeroes rather than omitting them, so the grid must place
        // them — dropping them would reintroduce the gap-inference bug the server rule exists to prevent.
        var days = Enumerable.Range(6, 7).Select(d => day($"2026-09-{d:00}", d == 8 ? 0 : 1));
        var grid = HeatmapGrid.Build(days);
        Assert.NotNull(grid.Weeks[0][2]);             // Tuesday exists
        Assert.Equal(0, grid.Weeks[0][2]?.Level);     // …and is drawn at level 0, not left blank
    }

    [Fact]
    public void testAMalformedDateIsSkippedRatherThanCrashing()
    {
        var grid = HeatmapGrid.Build([day("not-a-date", 3), day("2026-09-06", 1)]);
        var week = Assert.Single(grid.Weeks);
        Assert.Single(week, c => c is not null);
    }

    [Fact]
    public void testTheEmptyGridIsStillDrawn()
    {
        var grid = HeatmapGrid.Empty();
        Assert.Equal(22, grid.Weeks.Count);
        Assert.True(grid.IsEmpty);
        Assert.All(grid.Weeks, w => Assert.Equal(7, w.Count));
    }

    // MARK: - Cache (rule 22, AC-7)

    private InsightsCache tempCache()
    {
        // Removed in Dispose: every call used to leave a directory behind in the system temp folder.
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _tempDirs.Add(dir);
        return new InsightsCache(Path.Combine(dir, InsightsCache.FileName));
    }

    [Fact]
    public void testTheCacheRoundTripsTheResponseVerbatim()
    {
        var cache = tempCache();
        var original = insights(days: [day("2026-09-06", 2, words: 20)], apps: [new InsightsAppShare("Slack", 4, 100)]);
        cache.Save(original);
        Assert.Equal(original, cache.Load());
    }

    [Fact]
    public void testAMissingCacheIsNilNotAnError()
    {
        Assert.Null(tempCache().Load());
    }

    [Fact]
    public void testACorruptCacheIsTreatedAsAbsent()
    {
        // Parse failure -> loading state -> refresh. Never a crash, never a parse error on screen. One
        // refresh is the whole cost of a bad file.
        var cache = tempCache();
        Directory.CreateDirectory(Path.GetDirectoryName(cache.FilePath)!);
        File.WriteAllText(cache.FilePath, "{ this is not json");
        Assert.Null(cache.Load());
    }

    [Fact]
    public void testNoTranscriptTextCanReachTheCacheFile()
    {
        // The client half of AC-4. `Insights` has no field that could hold a transcript, so the file on
        // disk cannot contain one — the type, not just the server, keeps the "transcripts never touch this
        // disk" promise true.
        var cache = tempCache();
        cache.Save(insights(days: [day("2026-09-06", 1, words: 12)], apps: [new InsightsAppShare("Mail", 1, 100)]));
        var written = File.Exists(cache.FilePath) ? File.ReadAllText(cache.FilePath) : "";
        Assert.DoesNotContain("\"raw\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\"cleaned\"", written, StringComparison.Ordinal);
        Assert.NotEmpty(written);
    }

    // MARK: - The model

    [Fact]
    public Task testTheModelRendersTheCacheBeforeAnyRequestIsMade()
    {
        var cache = tempCache();
        var cached = insights(words: 4242);
        cache.Save(cached);
        var api = new StubApi { Offline = true };

        var model = new InsightsModel(api, cache);
        Assert.Equal(populated(cached), model.Presentation.Content);   // the window must open with numbers on it
        Assert.Empty(api.InsightsCalls);                                // …before the network is touched at all
        return Task.CompletedTask;
    }

    [Fact]
    public async Task testARefreshReplacesTheCacheAndClearsTheNotice()
    {
        var cache = tempCache();
        var fresh = insights(words: 9001);
        var api = new StubApi { InsightsResult = Result<Insights>.Success(fresh) };

        var model = new InsightsModel(api, cache);
        await model.Refresh();

        Assert.Equal(populated(fresh), model.Presentation.Content);
        Assert.Equal(none, model.Presentation.Notice);
        var call = Assert.Single(api.InsightsCalls);
        Assert.Equal(InsightsModel.Weeks, call.Weeks);
        // The user's own calendar days, not UTC.
        Assert.True(IanaTimeZone.TryGetIana(TimeZoneInfo.Local, out var localZone));
        Assert.Equal(localZone, call.Tz);
        await model.PendingSave!;
        Assert.Equal(fresh, cache.Load());
    }

    [Fact]
    public async Task testAFailedRefreshLeavesTheCachedNumbersOnScreen()
    {
        // AC-7 end to end on the client: cache present, server unreachable.
        var cache = tempCache();
        var cached = insights(words: 777);
        cache.Save(cached);
        var api = new StubApi { Offline = true };

        var model = new InsightsModel(api, cache);
        await model.Refresh();

        Assert.Equal(populated(cached), model.Presentation.Content);
        Assert.Equal<InsightsNotice>(new InsightsNotice.Unreachable(cached.GeneratedAt), model.Presentation.Notice);
        Assert.Equal(cached, cache.Load());   // a failed refresh must not touch the cache
    }

    [Fact]
    public async Task testAnUnauthorizedRefreshShowsTheTokenNotice()
    {
        var cache = tempCache();
        var api = new StubApi { InsightsResult = Result<Insights>.Failure(ApiException.Unauthorized()) };

        var model = new InsightsModel(api, cache);
        await model.Refresh();
        Assert.Equal<InsightsNotice>(new InsightsNotice.Unauthorized(), model.Presentation.Notice);
    }

    [Fact]
    public async Task testOverlappingRefreshesCollapseToOneRequest()
    {
        // Two refreshes in flight would only race each other to write the cache. The Mac holds the stub
        // for 40 ms; a gate holds it deterministically until all three calls have been made.
        var release = new TaskCompletionSource();
        var api = new StubApi { InsightsResult = Result<Insights>.Success(insights()), Gate = release.Task };
        var model = new InsightsModel(api, tempCache());

        var first = model.Refresh();
        var second = model.Refresh();
        var third = model.Refresh();
        release.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Single(api.InsightsCalls);
        Assert.Equal(none, model.Presentation.Notice);
        // The refresh succeeded, so it started a cache write. Left in flight it outlives this test and
        // recreates the temp directory Dispose has already removed.
        await model.PendingSave!;
    }

    // MARK: - Formatting

    [Fact]
    public void testLargeNumbersAreGrouped()
    {
        // A five-digit number needs a separator to be readable at a glance.
        Assert.NotEqual("64860", InsightsFormat.Number(64_860));
        Assert.Equal("7", InsightsFormat.Number(7, CultureInfo.CurrentCulture));
    }
}

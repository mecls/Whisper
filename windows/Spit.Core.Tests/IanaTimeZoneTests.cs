namespace Spit.Core.Tests;

/// Windows-only (rule 43, build spec invariant 7): the zone is sent as IANA or not at all — never UTC.
public sealed class IanaTimeZoneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// Custom zones rather than system lookups, so the id is exactly what the test names on every OS.
    private static TimeZoneInfo zone(string id) => TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.Zero, id, id);

    private sealed class ZoneTimeProvider(TimeZoneInfo zone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    [Fact]
    public void testAWindowsIdConvertsToIana()
    {
        Assert.True(IanaTimeZone.TryGetIana(zone("GMT Standard Time"), out var iana));
        Assert.Equal("Europe/London", iana);
    }

    [Fact]
    public void testAnIanaIdIsKeptAsIs()
    {
        Assert.True(IanaTimeZone.TryGetIana(zone("Europe/Lisbon"), out var iana));
        Assert.Equal("Europe/Lisbon", iana);
    }

    [Fact]
    public async Task testAnUnconvertibleZoneFailsAndTheModelMakesNoRequest()
    {
        var unknown = zone("Spit Test Zone");
        Assert.False(IanaTimeZone.TryGetIana(unknown, out var iana));
        Assert.NotEqual("UTC", iana);

        var api = new StubApi { InsightsResult = Result<Insights>.Success(
            new Insights(new InsightsTotals(1, 1, 1, null), new InsightsStreak(1, 1), [], [], 1)) };
        var model = new InsightsModel(api, new InsightsCache(Path.Combine(_dir, InsightsCache.FileName)), new ZoneTimeProvider(unknown));
        await model.Refresh();

        Assert.Empty(api.InsightsCalls);
        Assert.Null(model.TimeZoneIdentifier);
        Assert.Equal<InsightsNotice>(new InsightsNotice.TimeZoneUnknown(), model.Presentation.Notice);
        Assert.Equal(Strings.TimeZoneUnknown, InsightsPresentation.NoticeText(model.Presentation.Notice));
        Assert.Null(model.PendingSave);
    }
}

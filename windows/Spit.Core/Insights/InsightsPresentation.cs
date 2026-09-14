using System.Globalization;

namespace Spit.Core;

/// What the Insights view draws. Port of `InsightsPresentation.Content` (renamed: C# cannot nest a type
/// and a property of the same name).
public abstract record InsightsContent
{
    private InsightsContent() { }

    /// No numbers yet and a request in flight. Only ever visible on a genuine first run — with a cache
    /// present the view goes straight to `Empty` or `Populated`.
    public sealed record Loading : InsightsContent;

    /// Zero dictations. Not hypothetical: it is what the first person to open the window sees, so every
    /// card renders its frame with a dash rather than a spinner.
    public sealed record Empty : InsightsContent;

    public sealed record Populated(Insights Insights) : InsightsContent;
}

/// The one line under the title. Port of `InsightsPresentation.Notice`, plus Windows' time-zone case.
public abstract record InsightsNotice
{
    private InsightsNotice() { }

    public sealed record None : InsightsNotice;

    /// The refresh failed. Carries `generatedAt` from whatever numbers are on screen, so the line can say
    /// how old they are; null when there is nothing cached to be stale.
    public sealed record Unreachable(long? GeneratedAt) : InsightsNotice;

    /// A 401. Different copy, because "couldn't reach the server" would send the user to check their
    /// Wi-Fi when the fix is in Settings › Server.
    public sealed record Unauthorized : InsightsNotice;

    /// Rule 43 (Windows only): the zone has no IANA name, so no request was made. Never a UTC fallback.
    public sealed record TimeZoneUnknown : InsightsNotice;
}

/// What the window should draw, decided outside any view so it can be tested. Port of
/// mac/Voice/Insights/InsightsPresentation.swift.
///
/// The states are not independent — they are the product of "do we have numbers" and "did the last
/// refresh fail". Deriving them in one place keeps the window from ever showing a spinner that never
/// resolves, or an error dialog, both explicitly ruled out (insights rules 21 and 23).
public sealed record InsightsPresentation(InsightsContent Content, InsightsNotice Notice)
{
    public static InsightsPresentation Make(Insights? insights, bool isRefreshing, ApiException? failure, bool timeZoneUnknown = false)
    {
        InsightsContent content;
        if (insights is not null)
            content = insights.IsEmpty ? new InsightsContent.Empty() : new InsightsContent.Populated(insights);
        else if (isRefreshing)
            content = new InsightsContent.Loading();
        else
            // No cache and nothing in flight — a failed first refresh. The empty state is the honest thing
            // to draw; the notice says why it is empty.
            content = new InsightsContent.Empty();

        InsightsNotice notice;
        if (timeZoneUnknown) notice = new InsightsNotice.TimeZoneUnknown();
        else if (failure is null) notice = new InsightsNotice.None();
        else if (failure.Kind == ApiErrorKind.Unauthorized) notice = new InsightsNotice.Unauthorized();
        else notice = new InsightsNotice.Unreachable(insights?.GeneratedAt);

        return new InsightsPresentation(content, notice);
    }

    /// The notice's user copy (mac/Voice/Insights/InsightsView.swift), or null when there is none.
    public static string? NoticeText(InsightsNotice notice, DateTimeOffset? now = null) => notice switch
    {
        InsightsNotice.Unauthorized => Strings.TokenInvalidInsights,
        InsightsNotice.Unreachable { GeneratedAt: { } ms } => Strings.LastUpdated(InsightsFormat.RelativeTime(ms, now)),
        InsightsNotice.Unreachable => Strings.LastUpdatedNever,
        InsightsNotice.TimeZoneUnknown => Strings.TimeZoneUnknown,
        _ => null,
    };
}

public static class InsightsFormat
{
    /// Thousands-separated in the user's locale, so 64860 reads as 64,860 rather than a wall of digits the
    /// eye has to count.
    public static string Number(int n, CultureInfo? culture = null) => n.ToString("N0", culture ?? CultureInfo.CurrentCulture);

    /// "2 minutes ago", "in 3 days" — the numeric style of Foundation's `RelativeDateTimeFormatter`, in
    /// English like the rest of the UI. Takes the server's `generatedAt` (ms epoch), because the staleness
    /// line is about how old the numbers are, not how old the file is.
    public static string RelativeTime(long sinceMsEpoch, DateTimeOffset? now = null)
    {
        var seconds = ((now ?? DateTimeOffset.UtcNow) - DateTimeOffset.FromUnixTimeMilliseconds(sinceMsEpoch)).TotalSeconds;
        var abs = Math.Abs(seconds);
        const double minute = 60, hour = 3600, day = 86400;
        var (value, unit) = abs switch
        {
            < minute => ((long)abs, "second"),
            < hour => ((long)(abs / minute), "minute"),
            < day => ((long)(abs / hour), "hour"),
            < 7 * day => ((long)(abs / day), "day"),
            < 30 * day => ((long)(abs / (7 * day)), "week"),
            < 365 * day => ((long)(abs / (30 * day)), "month"),
            _ => ((long)(abs / (365 * day)), "year"),
        };
        var phrase = $"{value.ToString(CultureInfo.InvariantCulture)} {unit}{(value == 1 ? "" : "s")}";
        return seconds >= 0 ? $"{phrase} ago" : $"in {phrase}";
    }
}

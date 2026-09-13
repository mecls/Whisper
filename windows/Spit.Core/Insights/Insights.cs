namespace Spit.Core;

// Port of mac/Voice/Insights/Insights.swift: the `GET /v1/insights` response, verbatim.
//
// Numbers only, by design — the server never selects `raw` or `cleaned` for this endpoint, so no
// transcript text can reach this type or the cache file it is written to (rule 25).

public sealed record InsightsTotals(int Dictations, int Words, int AudioMs, int? Wpm);

public sealed record InsightsStreak(int Current, int Longest);

/// `Date` is YYYY-MM-DD in the requested zone.
public sealed record InsightsDay(string Date, int Dictations, int Words);

/// `Share` is an integer percent; the whole list sums to exactly 100.
public sealed record InsightsAppShare(string Label, int Dictations, int Share);

public sealed record Insights(
    InsightsTotals Totals,
    InsightsStreak Streak,
    IReadOnlyList<InsightsDay> Days,
    IReadOnlyList<InsightsAppShare> Apps,
    long GeneratedAt)
{
    public bool IsEmpty => Totals.Dictations == 0;

    // Records compare lists by reference; the Mac's struct compares by value, and tests rely on it.
    public bool Equals(Insights? other) =>
        other is not null
        && Totals == other.Totals
        && Streak == other.Streak
        && Days.SequenceEqual(other.Days)
        && Apps.SequenceEqual(other.Apps)
        && GeneratedAt == other.GeneratedAt;

    public override int GetHashCode() => HashCode.Combine(Totals, Streak, Days.Count, Apps.Count, GeneratedAt);
}

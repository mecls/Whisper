using System.Globalization;

namespace Spit.Core;

/// The contribution grid: weekday rows (Sun–Sat) by week columns. Port of
/// mac/Voice/Insights/HeatmapGrid.swift.
///
/// A plain value type rather than logic in a view, so the bucketing that decides which cell a day lands
/// in can be tested. Misplacing a day by one column is the classic failure here and it is invisible by
/// eye — every week looks plausible when it is wrong.
public sealed class HeatmapGrid : IEquatable<HeatmapGrid>
{
    /// `Level` is 0–3. Fixed thresholds, never relative to the busiest day in the window, so a quiet week
    /// does not repaint the whole picture darker and imply activity that did not happen.
    public sealed record Cell(string Date, int Dictations, int Level)
    {
        public string Id => Date;
    }

    public HeatmapGrid(IReadOnlyList<IReadOnlyList<Cell?>> weeks) => Weeks = weeks;

    /// Oldest week first; each column is exactly 7 entries, Sunday at index 0. A null is a day outside
    /// the window — the leading days of the first week, or the days after today.
    public IReadOnlyList<IReadOnlyList<Cell?>> Weeks { get; }

    public bool IsEmpty => Weeks.All(w => w.All(c => c is null || c.Dictations == 0));

    /// An all-blank grid of the right size, for the empty state. The frame is still drawn — an empty card
    /// that shows nothing where the chart goes reads as broken rather than as new.
    public static HeatmapGrid Empty(int weeks = 22) =>
        new(Enumerable.Range(0, weeks).Select(_ => (IReadOnlyList<Cell?>)new Cell?[7]).ToArray());

    /// Rule 18's four levels: 0, 1–2, 3–5, 6+.
    public static int Level(int dictations) => dictations switch
    {
        <= 0 => 0,
        <= 2 => 1,
        <= 5 => 2,
        _ => 3,
    };

    /// 0 = Sunday. Null for anything that is not a `YYYY-MM-DD` date.
    ///
    /// Computed in UTC, deliberately: the dates are already local calendar days in the user's zone, and
    /// re-reading them in the device's zone could shift one across a boundary and move a whole column.
    /// Lenient like Foundation's `Calendar` (month 13 rolls into the next year), so both clients place the
    /// same odd input in the same cell.
    public static int? WeekdayIndex(string date)
    {
        var parts = new List<int>(3);
        foreach (var p in date.Split('-', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(p, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) parts.Add(n);
        if (parts.Count != 3) return null;
        try
        {
            var d = new DateTime(parts[0], 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(parts[1] - 1).AddDays(parts[2] - 1);
            return (int)d.DayOfWeek;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// Builds the grid from the day list, starting a new column on every Sunday.
    ///
    /// The weekday is derived from each date rather than assumed from its position, so a cached response
    /// from an older server — or one that starts mid-week — still lands every day in the right row.
    public static HeatmapGrid Build(IEnumerable<InsightsDay> days)
    {
        var weeks = new List<IReadOnlyList<Cell?>>();
        var column = new Cell?[7];
        var columnHasDays = false;

        foreach (var day in days)
        {
            if (WeekdayIndex(day.Date) is not { } weekday) continue;
            if (weekday == 0 && columnHasDays)
            {
                weeks.Add(column);
                column = new Cell?[7];
                columnHasDays = false;
            }
            column[weekday] = new Cell(day.Date, day.Dictations, Level(day.Dictations));
            columnHasDays = true;
        }
        if (columnHasDays) weeks.Add(column);
        return new HeatmapGrid(weeks);
    }

    public bool Equals(HeatmapGrid? other) =>
        other is not null
        && Weeks.Count == other.Weeks.Count
        && Weeks.Zip(other.Weeks).All(pair => pair.First.SequenceEqual(pair.Second));

    public override bool Equals(object? obj) => Equals(obj as HeatmapGrid);

    public override int GetHashCode() => Weeks.Count;
}

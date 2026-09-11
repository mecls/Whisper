import Foundation

/// The contribution grid: weekday rows (Sun–Sat) by week columns.
///
/// A plain value type rather than logic inside a `View` body, so the bucketing that decides which
/// cell a day lands in can be tested. Misplacing a day by one column is the classic failure here
/// and it is invisible by eye — every week looks plausible when it is wrong.
struct HeatmapGrid: Equatable {
    struct Cell: Equatable, Identifiable {
        let date: String
        let dictations: Int
        /// 0–3. Fixed thresholds, never relative to the busiest day in the window, so a quiet week
        /// does not repaint the whole picture darker and imply activity that did not happen.
        let level: Int
        var id: String { date }
    }

    /// Oldest week first; each column is exactly 7 entries, Sunday at index 0. A `nil` is a day
    /// outside the window — the leading days of the first week, or the days after today.
    let weeks: [[Cell?]]

    var isEmpty: Bool { weeks.allSatisfy { $0.allSatisfy { $0 == nil || $0!.dictations == 0 } } }

    /// An all-blank grid of the right size, for the empty state. The frame is still drawn — an
    /// empty card that shows nothing where the chart goes reads as broken rather than as new.
    static func empty(weeks count: Int = 22) -> HeatmapGrid {
        HeatmapGrid(weeks: Array(repeating: [Cell?](repeating: nil, count: 7), count: count))
    }

    /// Rule 18's four levels: 0, 1–2, 3–5, 6+.
    static func level(forDictations n: Int) -> Int {
        switch n {
        case ...0: return 0
        case 1...2: return 1
        case 3...5: return 2
        default: return 3
        }
    }

    /// UTC, deliberately. The dates are already local calendar days in the user's zone; re-reading
    /// them in the device's current zone could shift one across a boundary and move a whole column.
    private static let calendar: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        return c
    }()

    /// 0 = Sunday. Returns nil for anything that is not a `YYYY-MM-DD` date.
    static func weekdayIndex(of date: String) -> Int? {
        let parts = date.split(separator: "-").compactMap { Int($0) }
        guard parts.count == 3,
              let d = calendar.date(from: DateComponents(year: parts[0], month: parts[1], day: parts[2]))
        else { return nil }
        return calendar.component(.weekday, from: d) - 1
    }

    /// Builds the grid from the day list, starting a new column on every Sunday.
    ///
    /// The weekday is derived from each date rather than assumed from its position, so a cached
    /// response from an older server — or one that starts mid-week — still lands every day in the
    /// right row instead of shifting the entire grid.
    static func build(days: [Insights.Day]) -> HeatmapGrid {
        var weeks: [[Cell?]] = []
        var column = [Cell?](repeating: nil, count: 7)
        var columnHasDays = false

        for day in days {
            guard let weekday = weekdayIndex(of: day.date) else { continue }
            if weekday == 0 && columnHasDays {
                weeks.append(column)
                column = [Cell?](repeating: nil, count: 7)
                columnHasDays = false
            }
            column[weekday] = Cell(date: day.date, dictations: day.dictations, level: level(forDictations: day.dictations))
            columnHasDays = true
        }
        if columnHasDays { weeks.append(column) }
        return HeatmapGrid(weeks: weeks)
    }
}

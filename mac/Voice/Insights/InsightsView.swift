import SwiftUI

/// The Insights window: four cards, numbers only.
///
/// Everything that could be got wrong — which state to show, which cell a day belongs in, how a
/// number is formatted — lives in `InsightsPresentation`, `HeatmapGrid` and `InsightsFormat`, and
/// is tested there. What is left here is layout.
struct InsightsView: View {
    @ObservedObject var model: InsightsModel

    private let columns = [GridItem(.flexible(), spacing: 16), GridItem(.flexible(), spacing: 16)]

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            notice
            ScrollView {
                LazyVGrid(columns: columns, spacing: 16) {
                    TotalWordsCard(content: model.presentation.content)
                    WordsPerMinuteCard(content: model.presentation.content)
                    StreakCard(content: model.presentation.content)
                    AppsCard(content: model.presentation.content)
                }
                .padding(20)
            }
        }
        .frame(minWidth: 720, minHeight: 520)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button(Strings.insightsRefresh, systemImage: "arrow.clockwise") { model.refresh() }
                    .keyboardShortcut("r", modifiers: .command)
            }
        }
        .navigationTitle(Strings.insightsTitle)
        .task { model.refresh() }
    }

    /// One line under the title. Never a dialog and never an alert: a dashboard that interrupts
    /// you because it could not reach a server is worse than one that quietly says so (rule 23).
    @ViewBuilder private var notice: some View {
        switch model.presentation.notice {
        case .none:
            EmptyView()
        case .unauthorized:
            NoticeBar(text: Strings.tokenInvalidInsights, systemImage: "exclamationmark.triangle.fill") {
                SettingsLink { Text(Strings.settings) }.buttonStyle(.link)
            }
        case .unreachable(let generatedAt):
            NoticeBar(
                text: generatedAt.map { Strings.lastUpdated(InsightsFormat.relativeTime(sinceMsEpoch: $0)) } ?? Strings.lastUpdatedNever,
                systemImage: "wifi.slash",
            ) { EmptyView() }
        }
    }
}

private struct NoticeBar<Trailing: View>: View {
    let text: String
    let systemImage: String
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: systemImage)
            Text(text)
            trailing
            Spacer()
        }
        .font(.callout)
        .foregroundStyle(.secondary)
        .padding(.horizontal, 20)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quaternary.opacity(0.5))
    }
}

// MARK: - Card chrome

private struct Card<Content: View>: View {
    let title: String
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(title)
                .font(.subheadline.weight(.medium))
                .foregroundStyle(.secondary)
            content
            Spacer(minLength: 0)
        }
        .padding(20)
        .frame(minHeight: 190, alignment: .topLeading)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 12))
    }
}

/// The number every card is built around, plus the two states that replace it. `—` rather than `0`
/// for empty: zero is a measurement, and the app has not measured anything yet.
private struct Figure: View {
    let value: String?
    let caption: String
    let isLoading: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if isLoading {
                RoundedRectangle(cornerRadius: 6)
                    .fill(.quaternary)
                    .frame(width: 120, height: 40)
            } else {
                Text(value ?? Strings.noValue)
                    .font(.system(size: 42, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
            }
            Text(caption)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

private struct EmptyHint: View {
    var body: some View {
        Text(Strings.insightsEmpty)
            .font(.caption)
            .foregroundStyle(.tertiary)
            .fixedSize(horizontal: false, vertical: true)
    }
}

// MARK: - The four cards

private struct TotalWordsCard: View {
    let content: InsightsPresentation.Content

    var body: some View {
        Card(title: Strings.cardTotalWords) {
            switch content {
            case .loading:
                Figure(value: nil, caption: Strings.cardTotalWordsCaption, isLoading: true)
            case .empty:
                Figure(value: nil, caption: Strings.cardTotalWordsCaption, isLoading: false)
                EmptyHint()
            case .populated(let i):
                Figure(value: InsightsFormat.number(i.totals.words), caption: Strings.cardTotalWordsCaption, isLoading: false)
            }
        }
    }
}

private struct WordsPerMinuteCard: View {
    let content: InsightsPresentation.Content

    var body: some View {
        Card(title: Strings.cardWpm) {
            switch content {
            case .loading:
                Figure(value: nil, caption: Strings.cardWpmCaption, isLoading: true)
            case .empty:
                Figure(value: nil, caption: Strings.cardWpmCaption, isLoading: false)
                EmptyHint()
            case .populated(let i):
                // A nil wpm is under a minute of total audio: a dash, not a made-up rate.
                Figure(value: i.totals.wpm.map(InsightsFormat.number), caption: Strings.cardWpmCaption, isLoading: false)
            }
        }
    }
}

private struct StreakCard: View {
    let content: InsightsPresentation.Content

    var body: some View {
        Card(title: Strings.cardStreak) {
            switch content {
            case .loading:
                Figure(value: nil, caption: "", isLoading: true)
            case .empty:
                Text(Strings.dayStreak(0))
                    .font(.system(size: 26, weight: .semibold, design: .rounded))
                HeatmapView(grid: .empty())
                EmptyHint()
            case .populated(let i):
                Text(Strings.dayStreak(i.streak.current))
                    .font(.system(size: 26, weight: .semibold, design: .rounded))
                    .monospacedDigit()
                HeatmapView(grid: HeatmapGrid.build(days: i.days))
                Text(Strings.longestStreak(i.streak.longest))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }
}

/// Fixed cell size, scrolling horizontally, newest week pinned right.
///
/// Dropping weeks at narrow widths was the alternative and it is not acceptable: a heatmap that
/// shows fewer weeks at one window size than another is a chart that changes its own story when
/// you resize it.
private struct HeatmapView: View {
    let grid: HeatmapGrid

    private let cell: CGFloat = 11
    private let gap: CGFloat = 3

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(alignment: .top, spacing: gap) {
                    ForEach(Array(grid.weeks.enumerated()), id: \.offset) { index, week in
                        VStack(spacing: gap) {
                            ForEach(0..<7, id: \.self) { row in
                                RoundedRectangle(cornerRadius: 2)
                                    .fill(fill(for: week.indices.contains(row) ? week[row] : nil))
                                    .frame(width: cell, height: cell)
                            }
                        }
                        .id(index)
                    }
                }
                .padding(.vertical, 2)
            }
            .onAppear {
                // The newest week is the one the user came to look at, so it must be on screen
                // without scrolling even when the card is too narrow for the whole range.
                if !grid.weeks.isEmpty { proxy.scrollTo(grid.weeks.count - 1, anchor: .trailing) }
            }
        }
        .frame(height: cell * 7 + gap * 6 + 4)
    }

    private func fill(for cell: HeatmapGrid.Cell?) -> Color {
        guard let cell else { return .clear }
        switch cell.level {
        case 1: return .accentColor.opacity(0.35)
        case 2: return .accentColor.opacity(0.65)
        case 3: return .accentColor
        default: return Color.secondary.opacity(0.15)
        }
    }
}

private struct AppsCard: View {
    let content: InsightsPresentation.Content

    var body: some View {
        Card(title: Strings.cardApps) {
            switch content {
            case .loading:
                Figure(value: nil, caption: "", isLoading: true)
            case .empty:
                Text(Strings.noValue)
                    .font(.system(size: 26, weight: .semibold, design: .rounded))
                EmptyHint()
            case .populated(let i):
                VStack(alignment: .leading, spacing: 7) {
                    ForEach(i.apps) { app in
                        HStack(spacing: 10) {
                            Text(app.label)
                                .font(.callout)
                                .lineLimit(1)
                                .truncationMode(.middle)
                                .frame(width: 110, alignment: .leading)
                            GeometryReader { geo in
                                RoundedRectangle(cornerRadius: 3)
                                    .fill(.tint)
                                    .frame(width: max(2, geo.size.width * CGFloat(app.share) / 100), height: 8)
                                    .frame(maxHeight: .infinity, alignment: .center)
                            }
                            .frame(height: 12)
                            Text("\(app.share)%")
                                .font(.caption)
                                .monospacedDigit()
                                .foregroundStyle(.secondary)
                                .frame(width: 40, alignment: .trailing)
                        }
                    }
                }
            }
        }
    }
}

import SwiftUI

/// The desktop app: insights and settings in one window, chosen from a sidebar.
///
/// Settings used to live only in the standard `Settings` scene behind ⌘,. That is the right place
/// for a menu-bar utility, and the wrong one for an app with a dock icon and a window — it means
/// everything the app can do is hidden behind a keyboard shortcut, and the window itself is a
/// read-only dashboard you visit once. Putting settings in the window makes the window the app
/// rather than a report about it.
///
/// The sidebar names the two things the window is, not the six screens it contains. The five
/// settings panes were briefly sidebar rows of their own, which made a five-item list out of a
/// single idea and left Insights as a lone entry above it. They are tabs inside the Settings pane
/// instead — the very same `SettingsTabs` the ⌘, window shows, so there is one settings UI, not two.
///
/// The ⌘, scene is kept as well: it costs nothing (the same views), and people reach for it.
struct MainWindow: View {
    @ObservedObject var coordinator: Coordinator
    @ObservedObject var sync: SyncService
    @State private var section: Section? = .insights

    enum Section: String, CaseIterable, Identifiable, Hashable {
        case insights, settings
        var id: String { rawValue }

        var label: String {
            switch self {
            case .insights: Strings.insightsTitle
            case .settings: Strings.settingsNav
            }
        }

        var icon: String {
            switch self {
            case .insights: "chart.bar.fill"
            case .settings: "gear"
            }
        }
    }

    var body: some View {
        NavigationSplitView {
            List(selection: $section) {
                ForEach(Section.allCases) { s in
                    Label(s.label, systemImage: s.icon).tag(s)
                }
            }
            .navigationSplitViewColumnWidth(min: 168, ideal: 184, max: 220)
        } detail: {
            detail
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .frame(minWidth: 720, minHeight: 520)
    }

    @ViewBuilder private var detail: some View {
        switch section ?? .insights {
        case .insights: InsightsView(model: coordinator.insights)
        // No `.padding(20)` here, unlike the panes this replaced. `TabView` already insets its
        // content, so padding on top of it both double-pads and — the part that bites — narrows
        // what captions have to wrap in. `SettingsNoteTests` measures that budget; see the note
        // on `SettingsNote` for what happens when a caption outgrows it.
        case .settings: SettingsTabs(coordinator: coordinator, sync: sync)
        }
    }
}

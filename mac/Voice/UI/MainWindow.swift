import SwiftUI

/// The desktop app: insights and settings in one window, chosen from a sidebar.
///
/// Settings used to live only in the standard `Settings` scene behind ⌘,. That is the right place
/// for a menu-bar utility, and the wrong one for an app with a dock icon and a window — it means
/// everything the app can do is hidden behind a keyboard shortcut, and the window itself is a
/// read-only dashboard you visit once. Flattening the five settings tabs into the sidebar makes the
/// window the app rather than a report about it.
///
/// The ⌘, scene is kept as well: it costs nothing (the same views), and people reach for it.
struct MainWindow: View {
    @ObservedObject var coordinator: Coordinator
    @ObservedObject var sync: SyncService
    @State private var section: Section? = .insights

    enum Section: String, CaseIterable, Identifiable, Hashable {
        case insights, general, model, server, dictionary, permissions
        var id: String { rawValue }

        var label: String {
            switch self {
            case .insights: Strings.insightsTitle
            case .general: Strings.settingsTabGeneral
            case .model: Strings.settingsTabModel
            case .server: Strings.settingsTabServer
            case .dictionary: Strings.settingsTabDictionary
            case .permissions: Strings.settingsTabPermissions
            }
        }

        var icon: String {
            switch self {
            case .insights: "chart.bar.fill"
            case .general: "gear"
            case .model: "waveform"
            case .server: "network"
            case .dictionary: "textformat.abc"
            case .permissions: "lock.shield"
            }
        }
    }

    var body: some View {
        NavigationSplitView {
            List(selection: $section) {
                // Insights sits apart from the settings below it: it is the thing you open the
                // window to look at, not a thing you configure.
                Label(Section.insights.label, systemImage: Section.insights.icon)
                    .tag(Section.insights)

                SwiftUI.Section(Strings.settings) {
                    ForEach(Section.allCases.filter { $0 != .insights }) { s in
                        Label(s.label, systemImage: s.icon).tag(s)
                    }
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
        case .general: GeneralTab(coordinator: coordinator, sync: sync).padding(20)
        case .model: ModelTab(coordinator: coordinator).padding(20)
        case .server: ServerTab(sync: sync).padding(20)
        case .dictionary: DictionaryTab(coordinator: coordinator, sync: sync).padding(20)
        case .permissions: PermissionsTab().padding(20)
        }
    }
}

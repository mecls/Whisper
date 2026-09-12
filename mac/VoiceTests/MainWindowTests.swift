import SwiftUI
import XCTest
@testable import Voice

/// The window's sidebar is a two-item list: the thing you look at, and the thing you configure.
///
/// The five settings panes were briefly rows of their own here. That read as a six-destination app
/// with one dashboard and five knobs, when it is a two-part app whose second part happens to have
/// five tabs. These tests pin the two-row shape, so putting the panes back is something someone
/// argues for rather than a diff that slips through.
final class MainWindowTests: XCTestCase {

    func testTheSidebarIsInsightsAndSettingsAndNothingElse() {
        XCTAssertEqual(
            MainWindow.Section.allCases.map(\.rawValue), ["insights", "settings"],
            "The sidebar lists two destinations. Settings panes belong to SettingsTabs, not here."
        )
    }

    /// `Strings.settings` — "Settings…" — belongs to the menu item and the `SettingsLink`s, where
    /// the ellipsis is the platform promising that a window follows the click. The sidebar row
    /// swaps the detail pane in place and opens nothing, so it must not borrow that string.
    func testTheSidebarRowDoesNotPromiseAWindow() {
        XCTAssertEqual(MainWindow.Section.settings.label, Strings.settingsNav)
        XCTAssertFalse(
            MainWindow.Section.settings.label.contains("…"),
            "An ellipsis on a sidebar row promises a window that never opens."
        )
    }

    func testEverySidebarRowHasALabelAndAnIcon() {
        for section in MainWindow.Section.allCases {
            XCTAssertFalse(section.label.isEmpty, "\(section) has no label")
            XCTAssertFalse(section.icon.isEmpty, "\(section) has no SF Symbol")
        }
    }
}

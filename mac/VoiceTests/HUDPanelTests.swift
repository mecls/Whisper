import AppKit
import XCTest
@testable import Voice

/// Where the resting bar sits.
///
/// Reported: the bar looks slightly right of centre. It was — by 44pt, which is exactly half the
/// width of a left-hand Dock. `layoutToFit` centred on `visibleFrame.midX`, and `visibleFrame` is
/// the screen minus the Dock, so with the Dock on a side its midpoint is not the screen's midpoint.
///
/// The reason it shipped is worth keeping: with the Dock at the bottom — where it was developed —
/// `frame` and `visibleFrame` have identical horizontal extents, so the wrong rectangle gives the
/// right answer. These cases put the Dock on every edge so that stops being true.
final class HUDPanelTests: XCTestCase {

    /// The machine the bug was reported on: 1710pt wide, Dock on the left, 88pt of it.
    private let screen = NSRect(x: 0, y: 0, width: 1710, height: 1112)
    private let dockLeft = NSRect(x: 88, y: 0, width: 1622, height: 1073)
    private let bar = NSSize(width: 200, height: 40)

    private func centreX(_ origin: NSPoint, _ size: NSSize) -> CGFloat { origin.x + size.width / 2 }

    func testTheBarIsCentredOnTheScreenNotTheDockFreeArea() {
        let origin = HUDPanel.barOrigin(screen: screen, visible: dockLeft, size: bar)
        XCTAssertEqual(
            centreX(origin, bar), screen.midX, accuracy: 0.5,
            "The bar centred on \(centreX(origin, bar)) instead of the screen's \(screen.midX) — it is following visibleFrame, which a left Dock shifts right by half its width."
        )
    }

    func testARightHandDockDoesNotPushTheBarLeft() {
        let dockRight = NSRect(x: 0, y: 0, width: 1622, height: 1073)
        let origin = HUDPanel.barOrigin(screen: screen, visible: dockRight, size: bar)
        XCTAssertEqual(centreX(origin, bar), screen.midX, accuracy: 0.5)
    }

    /// The case that always passed, and hid the other two.
    func testABottomDockStillCentresTheBar() {
        let dockBottom = NSRect(x: 0, y: 70, width: 1710, height: 1042)
        let origin = HUDPanel.barOrigin(screen: screen, visible: dockBottom, size: bar)
        XCTAssertEqual(centreX(origin, bar), screen.midX, accuracy: 0.5)
    }

    /// A second display does not start at x = 0. Centring has to use that screen's own midpoint,
    /// not the desktop's, or the bar lands on the wrong monitor entirely.
    func testTheBarCentresOnASecondaryDisplayInGlobalCoordinates() {
        let secondary = NSRect(x: 1710, y: 0, width: 1920, height: 1080)
        let visible = NSRect(x: 1710, y: 0, width: 1920, height: 1055)
        let origin = HUDPanel.barOrigin(screen: secondary, visible: visible, size: bar)
        XCTAssertEqual(centreX(origin, bar), secondary.midX, accuracy: 0.5)
    }

    /// Vertical placement keeps using `visibleFrame`, and must: that is what holds the bar above a
    /// bottom Dock rather than behind it.
    func testTheBarSitsJustAboveTheDockNotTheScreenEdge() {
        let dockBottom = NSRect(x: 0, y: 70, width: 1710, height: 1042)
        let origin = HUDPanel.barOrigin(screen: screen, visible: dockBottom, size: bar)
        XCTAssertEqual(origin.y, 80, "Expected 10pt above the Dock's top edge (70), not the screen's (0).")
    }

    /// Half-pixel origins make the material background shimmer as the bar resizes.
    func testTheOriginIsWholePixels() {
        let odd = NSSize(width: 201, height: 41)
        let origin = HUDPanel.barOrigin(screen: screen, visible: dockLeft, size: odd)
        XCTAssertEqual(origin.x, origin.x.rounded())
        XCTAssertEqual(origin.y, origin.y.rounded())
    }
}

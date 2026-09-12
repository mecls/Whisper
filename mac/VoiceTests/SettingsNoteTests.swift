import SwiftUI
import XCTest
@testable import Voice

/// The settings window's captions must never ask the detail pane for more width than the window has.
///
/// This exists because of a real failure, not a theoretical one. The live-transcription note shipped
/// as `Text(...).fixedSize(horizontal: false, vertical: true)`, which reads like "let this caption
/// wrap" and inside a `NavigationSplitView` detail pane does the opposite: with no width proposed,
/// the text reports its ideal width as one unbroken line — 1109 points. The pane asked the window
/// for 1149 points beside a 184-point sidebar, `NavigationSplitView` could not satisfy it against a
/// 900-point window, and it rendered *both* columns empty. Clicking General blanked the whole
/// window until the app was relaunched.
///
/// What made it expensive is that every ordinary diagnostic said the app was fine: no crash report,
/// no exception, the view body evaluating normally, and the main thread idle in its run loop. Only
/// measuring the layout found it. So the measurement is the test.
final class SettingsNoteTests: XCTestCase {

    @MainActor
    private func idealSize<V: View>(of view: V) -> CGSize {
        let host = NSHostingView(rootView: view)
        host.layoutSubtreeIfNeeded()
        return host.fittingSize
    }

    /// What a `TabView` takes horizontally before its content is offered any width at all.
    ///
    /// Measured, not assumed. The settings panes used to sit directly in the detail pane under a
    /// 20-point padding we chose; they are now inside `SettingsTabs`, and the inset is AppKit's to
    /// pick. Writing today's number in as a literal is exactly how this guard would keep passing
    /// while the thing it guards against came back on a future macOS.
    @MainActor
    private var tabViewHorizontalInset: CGFloat {
        let content = Color.clear.frame(width: 200, height: 40)
        let tabbed = idealSize(of: TabView {
            content.tabItem { Label(Strings.settingsTabGeneral, systemImage: "gear") }
        })
        return max(0, tabbed.width - idealSize(of: content).width)
    }

    /// What a pane's own `.padding()` takes. Every tab body ends with one, *inside* the tab
    /// chrome, so a caption is offered the pane width minus both and neither can be left out.
    @MainActor
    private var panePadding: CGFloat {
        let content = Color.clear.frame(width: 200, height: 40)
        return max(0, idealSize(of: content.padding()).width - idealSize(of: content).width)
    }

    /// The detail pane's share of the smallest window `MainWindow` allows (720), once the widest
    /// the sidebar can be dragged (220), the tab chrome, and the pane's own padding are taken out.
    /// A caption wider than this cannot be laid out in the smallest window the user can make.
    @MainActor
    private var detailBudget: CGFloat { 720 - 220 - tabViewHorizontalInset - panePadding }

    /// Records the headroom the layout actually leaves. Moving the panes inside a `TabView` traded
    /// a padding we chose for chrome AppKit chooses; if that chrome ever grows enough to swallow
    /// the budget, this fails with both numbers attached rather than leaving the next caption
    /// author to rediscover it as a blank window.
    @MainActor
    func testTheTabChromeLeavesRoomForACaption() {
        XCTAssertGreaterThan(
            detailBudget, SettingsNote.idealWidth,
            "TabView chrome takes \(tabViewHorizontalInset)pt, leaving a \(detailBudget)pt budget — under SettingsNote's \(SettingsNote.idealWidth)pt ideal width. Captions no longer fit the smallest window."
        )
    }

    @MainActor
    func testTheLiveTranscriptionNoteFitsTheDetailPane() {
        let size = idealSize(of: SettingsNote(Strings.liveTranscriptionNote))
        XCTAssertLessThanOrEqual(
            size.width, detailBudget,
            "A settings caption is asking for \(size.width)pt of ideal width. Anything past \(detailBudget)pt cannot fit the detail pane in the smallest window, and NavigationSplitView answers that by rendering the window empty."
        )
    }

    /// Bounding the width is only half the fix: the caption also has to actually wrap, or it is
    /// merely clipped. `frame(maxWidth:)` alone passes the width assertion above and still fails
    /// here, which is exactly the wrong fix this test is here to reject.
    @MainActor
    func testTheNoteWrapsRatherThanBeingClippedToOneLine() {
        let size = idealSize(of: SettingsNote(Strings.liveTranscriptionNote))
        let oneLine = idealSize(of: Text("x").font(.caption)).height
        XCTAssertGreaterThan(
            size.height, oneLine * 2,
            "The note is \(size.height)pt tall — it is being laid out as a single line and clipped, not wrapped."
        )
    }

    /// Pins the mechanism itself, so a future caption written the old way fails here with the
    /// reason attached rather than as a blank window someone has to bisect.
    @MainActor
    func testFixedSizeIsWhyTheOldCaptionBrokeTheWindow() {
        let broken = idealSize(of: Text(Strings.liveTranscriptionNote).font(.caption)
            .fixedSize(horizontal: false, vertical: true))
        XCTAssertGreaterThan(broken.width, detailBudget, "If this no longer overflows, SwiftUI's behaviour changed and SettingsNote's reasoning should be re-checked.")
        XCTAssertLessThan(idealSize(of: SettingsNote(Strings.liveTranscriptionNote)).width, broken.width)
    }
}

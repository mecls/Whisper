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

    /// The detail pane's share of the smallest window `MainWindow` allows (720) once the sidebar
    /// (up to 220) and the pane's own 20-point padding are taken out. A caption wider than this
    /// cannot be laid out in the smallest window the user can make.
    private let detailBudget: CGFloat = 720 - 220 - 40

    @MainActor
    private func idealSize<V: View>(of view: V) -> CGSize {
        let host = NSHostingView(rootView: view)
        host.layoutSubtreeIfNeeded()
        return host.fittingSize
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

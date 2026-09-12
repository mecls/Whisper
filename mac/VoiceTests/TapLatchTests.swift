import XCTest
@testable import Voice

/// prd-hands-free-dictation.md §5's gesture table, and the boundary that table depends on.
///
/// Time is supplied explicitly rather than slept through — `TapLatch` takes timestamps as
/// parameters precisely so these can run in microseconds and never flake on a loaded machine.
final class TapLatchTests: XCTestCase {

    private let t0 = Date(timeIntervalSince1970: 1_000_000)
    private func at(_ ms: Int) -> Date { t0.addingTimeInterval(Double(ms) / 1000) }

    // MARK: - The table

    func testAHoldStopsOnReleaseAndNeverLatches() {
        // A real dictation. The release must stop it immediately: no window, no timer, nothing
        // added to the release→paste path that the whole sub-second effort exists to shorten.
        var latch = TapLatch()
        XCTAssertEqual(latch.handle(.press, at: at(0)), [.startDictation])
        XCTAssertEqual(latch.handle(.release, at: at(600)), [.endSession])
        XCTAssertFalse(latch.isLatched)
    }

    func testTapThenTapInsideTheWindowLatches() {
        var latch = TapLatch()
        XCTAssertEqual(latch.handle(.press, at: at(0)), [.startDictation])
        XCTAssertEqual(latch.handle(.release, at: at(150)), [.holdOpen])
        XCTAssertEqual(latch.handle(.press, at: at(200)), [.latch])
        XCTAssertTrue(latch.isLatched)
    }

    func testTapThenTapAfterTheWindowDoesNotLatch() {
        // 150 ms release opens a window until 450 ms; a press at 550 ms is a separate gesture.
        var latch = TapLatch()
        _ = latch.handle(.press, at: at(0))
        _ = latch.handle(.release, at: at(150))
        XCTAssertEqual(latch.handle(.press, at: at(550)), [.abandon, .startDictation])
        XCTAssertFalse(latch.isLatched)
    }

    func testAWindowThatExpiresAbandonsTheRecording() {
        var latch = TapLatch()
        _ = latch.handle(.press, at: at(0))
        _ = latch.handle(.release, at: at(150))
        XCTAssertEqual(latch.windowExpired(at: at(450)), [.abandon])
        XCTAssertFalse(latch.isActive)
    }

    func testALatchedSessionEndsOnKeyDownAndSwallowsTheRelease() {
        var latch = TapLatch()
        _ = latch.handle(.press, at: at(0))
        _ = latch.handle(.release, at: at(150))
        _ = latch.handle(.press, at: at(200))
        XCTAssertEqual(latch.handle(.release, at: at(260)), [], "the latching press's own release means nothing")

        XCTAssertEqual(latch.handle(.press, at: at(30_000)), [.endSession], "ends on key-down, not on release")
        XCTAssertEqual(latch.handle(.release, at: at(30_080)), [], "and its release must not start anything")
        XCTAssertFalse(latch.isActive)
    }

    func testCancelClearsAnyGesture() {
        // Esc during a latched session, and a shortcut chord during a hold. Both discard.
        for setUp in [{ (l: inout TapLatch) in _ = l.handle(.press, at: self.at(0)) },
                      { (l: inout TapLatch) in
                          _ = l.handle(.press, at: self.at(0))
                          _ = l.handle(.release, at: self.at(150))
                          _ = l.handle(.press, at: self.at(200))
                      }] {
            var latch = TapLatch()
            setUp(&latch)
            XCTAssertEqual(latch.handle(.cancel, at: at(5000)), [])
            XCTAssertFalse(latch.isActive)
            XCTAssertFalse(latch.isLatched)
        }
    }

    // MARK: - The boundary

    func testTheTapThresholdIsTheReducersMinimum() {
        // 399 ms is a tap, 401 ms is a dictation. Asserted against the constant rather than a
        // literal 400 — if the reducer's minimum ever moves, this must move with it, and a test
        // hard-coding 400 would keep passing while the two definitions silently diverged.
        let minimum = DictationMachine.minimumMs

        var short = TapLatch()
        _ = short.handle(.press, at: at(0))
        XCTAssertEqual(short.handle(.release, at: at(minimum - 1)), [.holdOpen],
                       "just under the minimum must open a window")

        var long = TapLatch()
        _ = long.handle(.press, at: at(0))
        XCTAssertEqual(long.handle(.release, at: at(minimum + 1)), [.endSession])
    }

    func testExactlyTheMinimumCountsAsAHold() {
        var latch = TapLatch()
        _ = latch.handle(.press, at: at(0))
        XCTAssertEqual(latch.handle(.release, at: at(DictationMachine.minimumMs)), [.endSession])
    }

    func testAPressExactlyOnTheDeadlineStillLatches() {
        // The window is inclusive. A user who double-taps at exactly 300 ms meant to latch, and
        // half a millisecond of scheduling jitter should not decide otherwise.
        var latch = TapLatch()
        _ = latch.handle(.press, at: at(0))
        _ = latch.handle(.release, at: at(100))
        XCTAssertEqual(latch.handle(.press, at: at(100 + TapLatch.windowMs)), [.latch])
    }

    // MARK: - Things that should not happen, but must not misbehave

    func testWindowExpiredIsIgnoredWhenNoWindowIsOpen() {
        // The timer can fire after a second press has already latched: the Coordinator cancels it,
        // but a cancellation that loses a race must not end the session the user is mid-way through.
        var latch = TapLatch()
        _ = latch.handle(.press, at: at(0))
        _ = latch.handle(.release, at: at(150))
        _ = latch.handle(.press, at: at(200))
        XCTAssertEqual(latch.windowExpired(at: at(450)), [], "a late timer must not abandon a latched session")
        XCTAssertTrue(latch.isLatched)
    }

    func testStrayReleaseAndDoublePressAreInert() {
        var latch = TapLatch()
        XCTAssertEqual(latch.handle(.release, at: at(0)), [], "a release with no press is meaningless")
        _ = latch.handle(.press, at: at(10))
        XCTAssertEqual(latch.handle(.press, at: at(20)), [], "the key cannot go down twice without coming up")
    }
}

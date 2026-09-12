import XCTest
@testable import Voice

final class HotkeyInterpreterTests: XCTestCase {
    func testFnHoldAndRelease() {
        var i = HotkeyInterpreter(choice: .fn)
        XCTAssertEqual(i.handle(.flags(fn: true, rightOption: false, rightCommand: false)), .press)
        XCTAssertEqual(i.handle(.flags(fn: false, rightOption: false, rightCommand: false)), .release)
    }

    func testOtherKeyWhileHeldCancelsAndSwallowsTheRelease() {
        var i = HotkeyInterpreter(choice: .fn)
        _ = i.handle(.flags(fn: true, rightOption: false, rightCommand: false))
        XCTAssertEqual(i.handle(.keyDown(96)), .cancel)           // F5
        XCTAssertNil(i.handle(.flags(fn: false, rightOption: false, rightCommand: false)))
        XCTAssertEqual(i.handle(.flags(fn: true, rightOption: false, rightCommand: false)), .press)
    }

    func testEscapeCancels() {
        var i = HotkeyInterpreter(choice: .fn)
        _ = i.handle(.flags(fn: true, rightOption: false, rightCommand: false))
        XCTAssertEqual(i.handle(.keyDown(53)), .cancel)
    }

    func testKeysWhileIdleAreIgnored() {
        var i = HotkeyInterpreter(choice: .fn)
        XCTAssertNil(i.handle(.keyDown(0)))
        XCTAssertNil(i.handle(.flags(fn: false, rightOption: true, rightCommand: false)))
    }

    func testRightOptionChoiceIgnoresFn() {
        var i = HotkeyInterpreter(choice: .rightOption)
        XCTAssertNil(i.handle(.flags(fn: true, rightOption: false, rightCommand: false)))
        XCTAssertEqual(i.handle(.flags(fn: false, rightOption: true, rightCommand: false)), .press)
        XCTAssertEqual(i.handle(.flags(fn: false, rightOption: false, rightCommand: false)), .release)
    }

    // D2: isHeld is true after press, false after release — HotkeyMonitor.setChoice relies on this
    // to decide whether a Settings hotkey change must be deferred to the next release/cancel.
    func testIsHeldReflectsPressAndRelease() {
        var i = HotkeyInterpreter(choice: .fn)
        XCTAssertFalse(i.isHeld)
        _ = i.handle(.flags(fn: true, rightOption: false, rightCommand: false))
        XCTAssertTrue(i.isHeld)
        _ = i.handle(.flags(fn: false, rightOption: false, rightCommand: false))
        XCTAssertFalse(i.isHeld)
    }

    // H1: press → Esc (cancel) resets isHeld immediately — it must not wait for the physical
    // flags-release that follows, which is swallowed (returns nil) and produces no further action.
    func testIsHeldResetsOnCancelBeforeThePhysicalRelease() {
        var i = HotkeyInterpreter(choice: .fn)
        _ = i.handle(.flags(fn: true, rightOption: false, rightCommand: false))
        XCTAssertTrue(i.isHeld)
        XCTAssertEqual(i.handle(.keyDown(HotkeyInterpreter.escapeKeyCode)), .cancel)
        XCTAssertFalse(i.isHeld)
        XCTAssertNil(i.handle(.flags(fn: false, rightOption: false, rightCommand: false)))
        XCTAssertFalse(i.isHeld)
    }

    // MARK: - Latched mode (prd-hands-free-dictation.md rule 6)

    func testALatchedSessionIgnoresOrdinaryKeystrokes() {
        // The entire point of hands-free is that the user keeps typing. Before this, any keyDown
        // during a dictation cancelled it, so a latched session would have died on the first
        // keystroke — the one thing latched mode exists to allow.
        var i = HotkeyInterpreter(choice: .fn)
        i.latched = true
        for code: UInt16 in [0, 1, 36, 49, 123] {   // a, s, return, space, left-arrow
            XCTAssertNil(i.handle(.keyDown(code)), "keyCode \(code) must not cancel a latched session")
        }
    }

    func testEscStillCancelsALatchedSession() {
        // The single exception. Once the key is no longer held there is no other way to say
        // "throw this away" — and a 30-second hands-free session is exactly when you want one.
        var i = HotkeyInterpreter(choice: .fn)
        i.latched = true
        XCTAssertEqual(i.handle(.keyDown(HotkeyInterpreter.escapeKeyCode)), .cancel)
    }

    func testUnlatchedBehaviourIsUnchanged() {
        // The hold path must be untouched: during a hold, any key is a shortcut and cancels.
        var i = HotkeyInterpreter(choice: .fn)
        XCTAssertEqual(i.handle(.flags(fn: true, rightOption: false, rightCommand: false)), .press)
        XCTAssertEqual(i.handle(.keyDown(0)), .cancel)
    }
}

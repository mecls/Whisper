import XCTest
@testable import Voice

/// prd-insights-dashboard.md rule 3 / build spec invariant 14: a dictation must never be pasted
/// into Voice itself.
///
/// This became possible the moment Voice grew a dock icon and a window. Before that the app had no
/// focusable window, so "the frontmost app is Voice" could not happen; now it can, and the failure
/// is silent — the transcript would be typed into the Insights window, land nowhere, and be gone
/// before the user understood what they were looking at.
final class TextInjectorRoutingTests: XCTestCase {

    /// The test bundle is hosted inside Voice.app, so `Bundle.main` here is the app itself — the
    /// same value the injector compares against at runtime.
    private var own: String { TextInjector.ownBundleId }

    func testDictationIntoVoiceItselfGoesToTheClipboard() {
        XCTAssertTrue(TextInjector.mustUseClipboard(targetBundleId: own, secureInputActive: false))
    }

    func testOrdinaryAppsStillGetAPaste() {
        for bundleId in ["com.apple.mail", "com.tinyspeck.slackmacgap", "com.apple.Notes", "com.apple.dt.Xcode"] {
            XCTAssertFalse(
                TextInjector.mustUseClipboard(targetBundleId: bundleId, secureInputActive: false),
                "\(bundleId) must still receive a normal paste",
            )
        }
    }

    func testAnUnknownTargetPastesRatherThanBeingTreatedAsVoice() {
        // `FrontmostContext.current()` returns nil when there is no frontmost app, and an app can
        // report a nil bundle id. Reading "I don't know" as "it might be me" would divert ordinary
        // dictations to the clipboard for no reason the user could see.
        XCTAssertFalse(TextInjector.mustUseClipboard(targetBundleId: nil, secureInputActive: false))
    }

    func testSecureInputStillWinsForEveryTarget() {
        // The pre-existing reason for the clipboard path, unchanged: a secure field cannot receive
        // a synthetic ⌘V at all, whichever app owns it.
        XCTAssertTrue(TextInjector.mustUseClipboard(targetBundleId: nil, secureInputActive: true))
        XCTAssertTrue(TextInjector.mustUseClipboard(targetBundleId: "com.apple.Terminal", secureInputActive: true))
        XCTAssertTrue(TextInjector.mustUseClipboard(targetBundleId: own, secureInputActive: true))
    }

    func testOwnBundleIdIsResolvedFromTheBundleNotHardcodedTwice() {
        XCTAssertEqual(own, "co.miraside.voice")
    }
}

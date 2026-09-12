import AppKit
import SwiftUI

/// The persistent bar. A non-activating floating panel that never takes focus, visible over
/// full-screen apps and on every Space.
///
/// It used to appear for a dictation and hide 1.2 s later. It is now always on screen, which
/// changes two things fundamentally:
///
/// - **It is clickable**, so `ignoresMouseEvents` can no longer be `true`. That flag was the
///   guarantee that the panel could never swallow a click meant for the window underneath, so the
///   guarantee is reinstated by shape instead: the panel's frame is exactly the visible bar, never
///   a wide transparent container, and every view inside it except the mic button sets
///   `.allowsHitTesting(false)`.
/// - **It must never activate the app.** `.nonactivatingPanel` plus `canBecomeKey`/`canBecomeMain`
///   returning false is what keeps the dictation's captured target app pointing at whatever the
///   user was actually typing in. If a click activated Voice, that target would become Voice
///   itself, `TextInjector.mustUseClipboard` would correctly divert the text to the clipboard, and
///   the dictation would silently not appear where the user was looking.
final class HUDPanel: NSPanel {
    private let model = HUDModel()
    private var observers: [NSObjectProtocol] = []

    /// Called when the mic button is clicked. Wired to the same session entry points the keyboard
    /// uses, so a session started by mouse can be ended by key and vice versa.
    var onMicTap: (() -> Void)? {
        get { model.onMicTap }
        set { model.onMicTap = newValue }
    }

    init() {
        super.init(contentRect: NSRect(origin: .zero, size: HUDView.idleSize),
                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        ignoresMouseEvents = false
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        contentView = NSHostingView(rootView: HUDView(model: model))

        // The bar follows keyboard focus across displays. `NSScreen.main` is documented as the
        // screen containing the window with keyboard focus, which is exactly the rule — not the
        // screen with the menu bar, and not the one under the mouse pointer, which would make an
        // always-visible bar jump between displays as the cursor moved.
        let reposition: (Notification) -> Void = { [weak self] _ in self?.position() }
        observers = [
            NotificationCenter.default.addObserver(
                forName: NSApplication.didChangeScreenParametersNotification,
                object: nil, queue: .main, using: reposition),
            NSWorkspace.shared.notificationCenter.addObserver(
                forName: NSWorkspace.didActivateApplicationNotification,
                object: nil, queue: .main, using: reposition),
        ]
    }

    deinit {
        for o in observers { NotificationCenter.default.removeObserver(o) }
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    /// Renders the current state. Visibility is governed by `Preferences.showBar` alone — the bar
    /// no longer appears and disappears with the dictation.
    func update(_ state: HUDState, levels: [Float], latched: Bool) {
        model.state = state
        model.levels = levels
        model.latched = latched
        model.mode = Preferences.mode
        model.language = Preferences.language
        applyVisibility()
    }

    /// Called when `Preferences.showBar` changes, so the toggle takes effect without a dictation.
    func applyVisibility() {
        guard Preferences.showBar else { return orderOut(nil) }
        resize(for: model.state)
        position()
        if !isVisible { orderFrontRegardless() }
    }

    private func resize(for state: HUDState) {
        let target = HUDView.size(for: state)
        guard frame.size != target else { return }
        setContentSize(target)
    }

    private func position() {
        guard let f = (NSScreen.main ?? NSScreen.screens.first)?.visibleFrame else { return }
        // `visibleFrame` already excludes the Dock; the extra 40 pt keeps the bar clear of it
        // rather than flush against it.
        setFrameOrigin(NSPoint(x: f.midX - frame.width / 2, y: f.minY + 40))
    }
}

final class HUDModel: ObservableObject {
    @Published var state: HUDState = .hidden
    @Published var levels: [Float] = []
    @Published var latched = false
    @Published var mode: String = Preferences.mode
    @Published var language: String = Preferences.language
    var onMicTap: (() -> Void)?
}

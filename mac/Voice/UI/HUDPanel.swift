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
    // What the current frame was measured for. Levels arrive ~47x/second and never change the
    // pill's width — the waveform is a fixed count of fixed-width bars — so re-measuring on every
    // one of them would force a layout pass 47 times a second for no possible change.
    private var laidOutFor: (state: HUDState, liveChars: Int, latched: Bool)?

    /// Called when the mic button is clicked. Wired to the same session entry points the keyboard
    /// uses, so a session started by mouse can be ended by key and vice versa.
    var onMicTap: (() -> Void)? {
        get { model.onMicTap }
        set { model.onMicTap = newValue }
    }

    init() {
        super.init(contentRect: NSRect(x: 0, y: 0, width: 120, height: 32),
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
        let reposition: (Notification) -> Void = { [weak self] _ in self?.layoutToFit() }
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
    func update(_ state: HUDState, levels: [Float], latched: Bool, liveText: String? = nil) {
        model.state = state
        model.levels = levels
        model.latched = latched
        model.liveText = liveText

        guard Preferences.showBar else { return orderOut(nil) }
        let key = (state: state, liveChars: liveText?.count ?? -1, latched: latched)
        let changed = laidOutFor.map {
            $0.state != key.state || $0.liveChars != key.liveChars || $0.latched != key.latched
        } ?? true
        if changed {
            laidOutFor = key
            layoutToFit()
        }
        if !isVisible { orderFrontRegardless() }
    }

    /// Called when `Preferences.showBar` changes, so the toggle takes effect without a dictation.
    func applyVisibility() {
        guard Preferences.showBar else { return orderOut(nil) }
        laidOutFor = nil          // force a re-measure: the bar may have been hidden since
        layoutToFit()
        if !isVisible { orderFrontRegardless() }
    }

    /// Sizes and centres the pill in one step, from one measurement.
    ///
    /// These were two calls and that was the bug: `setContentSize` keeps the window's bottom-left
    /// origin, so the bar grew rightward, and the centring that followed read `frame.width` — which
    /// SwiftUI had not yet updated, because it lays out asynchronously. The window was therefore
    /// centred using a width it was about to stop having, and drifted further the bigger the change,
    /// which is why a long dictation ending looked worst.
    ///
    /// `layoutSubtreeIfNeeded()` forces the pending layout so `fittingSize` is current, and
    /// `setFrame` applies origin and size together so no intermediate state is ever displayed.
    /// Where the bar sits, from the screen's two rectangles and the size SwiftUI measured.
    ///
    /// Pulled out of `layoutToFit` because it is pure geometry that otherwise needs a live
    /// `NSScreen` to exercise — and because the bug it had was invisible on the machine it was
    /// written on. With the Dock at the bottom, `frame` and `visibleFrame` agree horizontally, so
    /// centring on either looks identical. Move the Dock to a side and they stop agreeing.
    ///
    /// The two axes deliberately read different rectangles:
    ///
    /// - **x** from `frame`, the physical display. "Centred" means centred on the screen the user
    ///   is looking at. `visibleFrame` begins after a left-hand Dock, so its midpoint sits half a
    ///   Dock-width to the right — 44pt on the 88pt Dock this was reported against.
    /// - **y** from `visibleFrame`, which is the right rectangle there and the reason the two were
    ///   ever conflated: it already excludes a bottom Dock, so sitting 10pt above its `minY` keeps
    ///   the bar clear of the Dock, or against the screen edge when the Dock is hidden or on a side.
    ///
    /// Rounded because a half-pixel origin makes the material background shimmer as the bar resizes.
    static func barOrigin(screen: NSRect, visible: NSRect, size: NSSize) -> NSPoint {
        NSPoint(x: (screen.midX - size.width / 2).rounded(),
                y: (visible.minY + 10).rounded())
    }

    private func layoutToFit() {
        guard let host = contentView else { return }
        host.layoutSubtreeIfNeeded()
        let size = host.fittingSize
        guard size.width > 1, size.height > 1 else { return }
        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }

        let origin = Self.barOrigin(screen: screen.frame, visible: screen.visibleFrame, size: size)
        let target = NSRect(origin: origin, size: size)
        guard frame != target else { return }
        setFrame(target, display: true)
    }

}

final class HUDModel: ObservableObject {
    @Published var state: HUDState = .hidden
    @Published var levels: [Float] = []
    @Published var latched = false
    /// Streaming transcript, already filtered by `showTextInHUD` before it reaches here.
    @Published var liveText: String?
    var onMicTap: (() -> Void)?
}

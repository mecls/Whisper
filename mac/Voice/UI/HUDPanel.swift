import AppKit
import SwiftUI

/// Non-activating floating panel: never takes focus (so the paste lands in the user's app),
/// visible over full-screen apps, on every Space.
final class HUDPanel: NSPanel {
    private let model = HUDModel()

    init() {
        super.init(contentRect: NSRect(x: 0, y: 0, width: 320, height: 64),
                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        ignoresMouseEvents = true
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        contentView = NSHostingView(rootView: HUDView(model: model))
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    func show(_ state: HUDState, levels: [Float]) {
        model.state = state
        model.levels = levels
        if !isVisible { position(); orderFrontRegardless() }
    }

    func hide() { orderOut(nil) }

    private func position() {
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { $0.frame.contains(mouse) } ?? NSScreen.main
        guard let f = screen?.visibleFrame else { return }
        setFrameOrigin(NSPoint(x: f.midX - frame.width / 2, y: f.minY + 40))
    }
}

final class HUDModel: ObservableObject {
    @Published var state: HUDState = .hidden
    @Published var levels: [Float] = []
}

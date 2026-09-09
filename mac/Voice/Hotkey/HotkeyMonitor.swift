import AppKit
import CoreGraphics

/// Owns the tap and the interpreter. Idle: flagsChanged only. Listening: flagsChanged + keyDown.
final class HotkeyMonitor {
    private let tap = EventTap()
    private var interpreter: HotkeyInterpreter
    private var listening = false
    var onAction: ((HotkeyAction) -> Void)?

    private static let idleMask: CGEventMask = 1 << CGEventType.flagsChanged.rawValue
    private static let listeningMask: CGEventMask = (1 << CGEventType.flagsChanged.rawValue) | (1 << CGEventType.keyDown.rawValue)

    init(choice: HotkeyChoice) {
        interpreter = HotkeyInterpreter(choice: choice)
        tap.onEvent = { [weak self] type, event in self?.handle(type, event) }
    }

    func setChoice(_ choice: HotkeyChoice) { interpreter = HotkeyInterpreter(choice: choice) }

    @discardableResult
    func start() -> Bool { tap.start(mask: Self.idleMask) }

    func stop() { tap.stop() }

    /// Called by the coordinator when a dictation starts/ends so keystrokes are only observed while listening.
    func setListening(_ on: Bool) {
        guard on != listening else { return }
        listening = on
        tap.start(mask: on ? Self.listeningMask : Self.idleMask)
    }

    private func handle(_ type: CGEventType, _ event: CGEvent) {
        let keyEvent: KeyEvent
        switch type {
        case .flagsChanged:
            let f = event.flags
            let code = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
            keyEvent = .flags(
                fn: f.contains(.maskSecondaryFn),
                rightOption: f.contains(.maskAlternate) && code == 61,
                rightCommand: f.contains(.maskCommand) && code == 54)
        case .keyDown:
            keyEvent = .keyDown(UInt16(event.getIntegerValueField(.keyboardEventKeycode)))
        default:
            return
        }
        if let action = interpreter.handle(keyEvent) {
            DispatchQueue.main.async { [weak self] in self?.onAction?(action) }
        }
    }
}

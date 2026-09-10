import AppKit
import CoreGraphics

/// Owns the tap and the interpreter. Idle: flagsChanged only. Listening: flagsChanged + keyDown.
final class HotkeyMonitor {
    private let tap = EventTap()
    private var interpreter: HotkeyInterpreter
    // D2: a choice picked in Settings while the current key is physically held is staged here and
    // applied on the interpreter's next `.release`/`.cancel`, instead of swapping the interpreter
    // (and orphaning the held key) mid-press.
    private var pendingChoice: HotkeyChoice?
    private var listening = false
    var onAction: ((HotkeyAction) -> Void)?

    private static let idleMask: CGEventMask = 1 << CGEventType.flagsChanged.rawValue
    private static let listeningMask: CGEventMask = (1 << CGEventType.flagsChanged.rawValue) | (1 << CGEventType.keyDown.rawValue)

    init(choice: HotkeyChoice) {
        interpreter = HotkeyInterpreter(choice: choice)
        tap.onEvent = { [weak self] type, event in self?.handle(type, event) }
    }

    // D2: a no-op (beyond staging `pendingChoice`) while a press is in flight — applied in `handle`
    // once the interpreter reports `.release`/`.cancel` for the currently-held key.
    func setChoice(_ choice: HotkeyChoice) {
        guard interpreter.isHeld else { interpreter = HotkeyInterpreter(choice: choice); return }
        pendingChoice = choice
    }

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
            // D2: the deferred choice (if any) is applied only once the key that was held is fully
            // released/cancelled — never mid-press.
            if (action == .release || action == .cancel), let pending = pendingChoice {
                interpreter = HotkeyInterpreter(choice: pending)
                pendingChoice = nil
            }
            DispatchQueue.main.async { [weak self] in self?.onAction?(action) }
        }
    }
}

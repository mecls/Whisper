enum KeyEvent: Equatable {
    case flags(fn: Bool, rightOption: Bool, rightCommand: Bool)
    case keyDown(UInt16)
}

enum HotkeyAction: Equatable { case press, release, cancel }

/// Turns modifier/key events into press/release/cancel. Pure so the rules are testable.
struct HotkeyInterpreter {
    static let escapeKeyCode: UInt16 = 53
    let choice: HotkeyChoice
    // D2: exposed so HotkeyMonitor can tell whether a `setChoice` while this key is held must be
    // deferred instead of orphaning the in-flight press.
    private(set) var isHeld = false

    init(choice: HotkeyChoice) { self.choice = choice }

    mutating func handle(_ e: KeyEvent) -> HotkeyAction? {
        switch e {
        case .flags(let fn, let ro, let rc):
            let down: Bool
            switch choice { case .fn: down = fn; case .rightOption: down = ro; case .rightCommand: down = rc }
            if down && !isHeld { isHeld = true; return .press }
            if !down && isHeld { isHeld = false; return .release }
            return nil
        case .keyDown:
            // Any real key while the hotkey is held means a shortcut (Fn+F5, Fn+←, Esc): not a
            // dictation. H1: `isHeld` resets right here, not at the swallowed physical release —
            // `.cancel` is the last action this press produces; the flags(down: false) that follows
            // it must fall through to `return nil` below (isHeld is already false by then), which is
            // also why a separate `cancelled` flag is no longer needed.
            guard isHeld else { return nil }
            isHeld = false
            return .cancel
        }
    }
}

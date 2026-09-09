import Carbon.HIToolbox

enum SecureInput {
    /// True while a password field (or Terminal's Secure Keyboard Entry) owns the keyboard.
    static var isActive: Bool { IsSecureEventInputEnabled() }
}

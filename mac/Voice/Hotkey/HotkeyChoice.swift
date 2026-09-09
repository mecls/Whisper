enum HotkeyChoice: String, CaseIterable, Codable {
    case fn, rightOption, rightCommand
    // Task 9: literals moved to Strings.swift (every user-facing string lives there).
    var label: String { Strings.hotkeyLabel(self) }
}

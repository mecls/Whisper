enum HotkeyChoice: String, CaseIterable, Codable {
    case fn, rightOption, rightCommand
    var label: String {
        switch self { case .fn: "Fn (🌐)"; case .rightOption: "Right ⌥"; case .rightCommand: "Right ⌘" }
    }
}

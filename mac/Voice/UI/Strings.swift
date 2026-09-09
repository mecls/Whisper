/// Every user-facing string. One file so a pt-PT pass later is one edit.
enum Strings {
    static let appName = "Voice"
    static let idle = "Ready — hold Fn to dictate"
    static let listening = "Listening…"
    static let transcribing = "Transcribing…"
    static let cleaning = "Cleaning…"
    static let done = "Done"
    static let nothingHeard = "Nothing heard"
    static let cancelled = "Cancelled"
    static let pastedRaw = "Pasted raw — server slow or offline"
    static let tokenInvalid = "Token invalid — open Settings"
    static let secureField = "Secure field — text copied to clipboard instead"
    static let modelLoading = "Model loading"
    static let modelNotDownloaded = "Model not downloaded"
    static let copyLast = "Copy last dictation"
    static let pause = "Pause dictation"
    static let resume = "Resume dictation"
    static let settings = "Settings…"
    static let quit = "Quit Voice"
    static let mode = "Mode"
    static let language = "Language"
    static let modeClean = "Clean"
    static let modeLiteral = "Literal"
    static let langAuto = "Auto"
    static let langPt = "Português"
    static let langEn = "English"

    static let setUpPermissions = "Set up permissions…"
    static let modelReady = "Model: ready"
    static func modelError(_ message: String) -> String { "Model error: \(message)" }
    static let modelLabelTurbo = "Large v3 Turbo (compressed, 630 MB) — recommended"
    static let modelLabelLarge = "Large v3 Turbo (full, ~1.5 GB) — maximum accuracy"

    // Onboarding
    static let onboardingTitle = "Set up Voice"
    static let permMicrophone = "Microphone"
    static let permInputMonitoring = "Input Monitoring (to see the Fn key)"
    static let permAccessibility = "Accessibility (to paste with ⌘V)"
    static let permFnKeyboard = "Keyboard › Press 🌐 key to → Do Nothing"
    static let onboardingRelaunchNote = "Some grants only take effect after a relaunch."
    static let relaunch = "Relaunch"
    static let onboardingDone = "Done"
    static let grant = "Grant"

    // Task 3: HotkeyChoice.label literals moved here per Task 9's controller ruling.
    static let hotkeyLabelFn = "Fn (🌐)"
    static let hotkeyLabelRightOption = "Right ⌥"
    static let hotkeyLabelRightCommand = "Right ⌘"
    static func hotkeyLabel(_ choice: HotkeyChoice) -> String {
        switch choice {
        case .fn: return hotkeyLabelFn
        case .rightOption: return hotkeyLabelRightOption
        case .rightCommand: return hotkeyLabelRightCommand
        }
    }

    // Settings window
    static let settingsTabGeneral = "General"
    static let settingsTabModel = "Model"
    static let settingsTabServer = "Server"
    static let settingsTabDictionary = "Dictionary"
    static let settingsTabPermissions = "Permissions"

    static let hotkeyPickerLabel = "Dictation key"
    static let playSounds = "Play sounds"
    static let showTextInHUDToggle = "Show text in the HUD after pasting"
    static let launchAtLogin = "Launch at login"
    static func launchAtLoginError(_ message: String) -> String { "Couldn't change Launch at Login: \(message)" }

    static let modelPickerLabel = "Model"
    static let downloadModel = "Download"
    static let deleteModel = "Delete"
    static let defaultLanguage = "Default language"

    static let serverURLLabel = "Server URL"
    static let serverURLChangeNote = "Changing the server URL requires relaunching Voice."
    static let apiTokenLabel = "API token"
    static let saveToken = "Save token"
    static let testConnection = "Test connection"
    static let signOut = "Sign out"
    static func connectedAs(_ name: String) -> String { "Connected as \(name)" }

    static let dictionaryTermPlaceholder = "Term"
    static let dictionaryReplacementPlaceholder = "Replacement (optional)"
    static let dictionaryTeamWide = "Team-wide"
    static let dictionaryAdd = "Add"
    static let dictionaryEmpty = "No dictionary terms yet"
    static let dictionaryLoadError = "Couldn't load the dictionary"
    static let dictionaryAddError = "Couldn't add the term"
    static let dictionaryDeleteError = "Couldn't delete the term"
}

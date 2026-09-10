import Foundation

/// UserDefaults-backed preferences. Views bind with `@AppStorage(Preferences.Key.sounds)` etc.;
/// non-view code reads these accessors. Server settings (mode/language/hotkey) are mirrored here only as an
/// offline cache; the server wins on every sync.
enum Preferences {
    enum Key {
        static let hotkey = "hotkey", sounds = "sounds", showTextInHUD = "showTextInHUD", modelId = "modelId"
        static let serverURL = "serverURL", mode = "mode", language = "language", onboarded = "onboarded"
    }
    static let defaults: [String: Any] = [
        Key.hotkey: HotkeyChoice.fn.rawValue, Key.sounds: true, Key.showTextInHUD: true,
        Key.modelId: "large-v3-v20240930_turbo_632MB", Key.serverURL: "https://voice.miraside.co",
        Key.mode: "clean", Key.language: "auto", Key.onboarded: false,
    ]
    static func registerDefaults() { UserDefaults.standard.register(defaults: defaults) }

    private static var d: UserDefaults { .standard }
    static var hotkey: HotkeyChoice {
        get { HotkeyChoice(rawValue: d.string(forKey: Key.hotkey) ?? "") ?? .fn }
        set { d.set(newValue.rawValue, forKey: Key.hotkey) }
    }
    static var sounds: Bool { get { d.bool(forKey: Key.sounds) } set { d.set(newValue, forKey: Key.sounds) } }
    static var showTextInHUD: Bool { get { d.bool(forKey: Key.showTextInHUD) } set { d.set(newValue, forKey: Key.showTextInHUD) } }
    static var modelId: String { get { d.string(forKey: Key.modelId) ?? "large-v3-v20240930_turbo_632MB" } set { d.set(newValue, forKey: Key.modelId) } }
    static var serverURL: String { get { d.string(forKey: Key.serverURL) ?? "https://voice.miraside.co" } set { d.set(newValue, forKey: Key.serverURL) } }
    static var mode: String { get { d.string(forKey: Key.mode) ?? "clean" } set { d.set(newValue, forKey: Key.mode) } }
    static var language: String { get { d.string(forKey: Key.language) ?? "auto" } set { d.set(newValue, forKey: Key.language) } }
    static var onboarded: Bool { get { d.bool(forKey: Key.onboarded) } set { d.set(newValue, forKey: Key.onboarded) } }
}

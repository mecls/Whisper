import Foundation

/// GET /v1/me on launch and every 10 minutes. Server settings win; the client writes through PUT.
/// C6: main-actor — the Timer is scheduled on main, and `sync()` writes `Preferences` and
/// `DictionaryCache.shared` directly.
@MainActor
final class SyncService: ObservableObject {
    @Published private(set) var userName: String?
    @Published private(set) var unauthorized = false
    private let api: VoiceAPIClient
    private var timer: Timer?
    // G3: the last settings object seen from (or successfully sent to) the server — `push` merges
    // onto this so a `llmModel` override set some other way (e.g. Task 9's model picker) survives a
    // mode/language-only push instead of being silently cleared.
    private var last: ServerSettings?
    // G1: a server-driven hotkey change must go through Coordinator.setHotkey (which owns writing
    // Preferences.hotkey AND re-arming the hotkey monitor) — SyncService itself never writes it.
    var onHotkeyChange: ((HotkeyChoice) -> Void)?

    init(api: VoiceAPIClient) { self.api = api }

    func start() {
        Task { await sync() }
        timer = Timer.scheduledTimer(withTimeInterval: 600, repeats: true) { [weak self] _ in Task { await self?.sync() } }
    }

    func sync() async {
        do {
            let me = try await api.me()
            userName = me.user.name
            unauthorized = false
            last = me.settings
            Preferences.mode = me.settings.mode
            Preferences.language = me.settings.language
            // Compared by rawValue — HotkeyChoice doesn't declare Equatable and adding it is outside
            // this round's touched files.
            if let h = HotkeyChoice(rawValue: me.settings.hotkey), h.rawValue != Preferences.hotkey.rawValue {
                onHotkeyChange?(h)
            }
            DictionaryCache.shared.update(me.dictionary.map { $0.replacement ?? $0.term })
        } catch APIError.unauthorized { unauthorized = true } catch {}
    }

    func push(mode: String? = nil, language: String? = nil, hotkey: HotkeyChoice? = nil) async {
        // User-initiated (or Coordinator-relayed) local writes happen regardless of whether the
        // network push below turns out to be a no-op.
        if let hotkey { Preferences.hotkey = hotkey }
        if let mode { Preferences.mode = mode }
        if let language { Preferences.language = language }

        // G3: merge onto the last known server settings (falling back to Preferences only if we've
        // never synced) so fields not being changed here — notably `llmModel` — are preserved.
        var merged = last ?? ServerSettings(mode: Preferences.mode, language: Preferences.language, hotkey: Preferences.hotkey.rawValue, llmModel: nil)
        if let mode { merged.mode = mode }
        if let language { merged.language = language }
        if let hotkey { merged.hotkey = hotkey.rawValue }

        // No-op guard: kills the VoiceApp `.onChange` echo when `sync()` itself just wrote this same
        // value into Preferences (which re-fires the picker's onChange, which calls push again).
        guard merged != last else { return }

        do {
            try await api.putSettings(merged)
            last = merged
        } catch {}
    }

    // D9: the Server tab's "Sign out" button calls this instead of assigning `unauthorized` itself
    // (it's `private(set)`). The sync timer keeps running; the next `sync()` will 401 and stay
    // unauthorized until a new token is saved.
    func signOut() {
        Keychain.delete(account: Preferences.serverURL)
        unauthorized = true
        userName = nil
    }
}

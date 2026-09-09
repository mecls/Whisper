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
            Preferences.mode = me.settings.mode
            Preferences.language = me.settings.language
            if let h = HotkeyChoice(rawValue: me.settings.hotkey) { Preferences.hotkey = h }
            DictionaryCache.shared.update(me.dictionary.map { $0.replacement ?? $0.term })
        } catch APIError.unauthorized { unauthorized = true } catch {}
    }

    func push(mode: String? = nil, language: String? = nil, hotkey: HotkeyChoice? = nil) async {
        if let mode { Preferences.mode = mode }
        if let language { Preferences.language = language }
        if let hotkey { Preferences.hotkey = hotkey }
        try? await api.putSettings(ServerSettings(mode: Preferences.mode, language: Preferences.language, hotkey: Preferences.hotkey.rawValue, llmModel: nil))
    }
}

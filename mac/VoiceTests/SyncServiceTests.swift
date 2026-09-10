import XCTest
@testable import Voice

// G5/C6: SyncService is @MainActor.
@MainActor
final class SyncServiceTests: XCTestCase {
    // Tests must not leave UserDefaults modified: snapshot every key `sync()`/`push()` can touch and
    // restore it verbatim in tearDown, regardless of what the test set it to.
    private var savedHotkey: String?
    private var savedMode: String?
    private var savedLanguage: String?

    override func setUpWithError() throws {
        let d = UserDefaults.standard
        savedHotkey = d.string(forKey: Preferences.Key.hotkey)
        savedMode = d.string(forKey: Preferences.Key.mode)
        savedLanguage = d.string(forKey: Preferences.Key.language)
    }

    override func tearDownWithError() throws {
        restore(Preferences.Key.hotkey, savedHotkey)
        restore(Preferences.Key.mode, savedMode)
        restore(Preferences.Key.language, savedLanguage)
    }

    private func restore(_ key: String, _ value: String?) {
        let d = UserDefaults.standard
        if let value { d.set(value, forKey: key) } else { d.removeObject(forKey: key) }
    }

    /// Builds a `MeResponse` by decoding a literal JSON payload shaped like the real wire format
    /// (rather than a memberwise init `MeResponse.Server` no longer has, since it decodes `allowedModels`
    /// tolerantly via a custom `init(from:)`).
    private func meResponse(mode: String, language: String, hotkey: String, llmModel: String? = nil) -> MeResponse {
        let llmModelJSON = llmModel.map { "\"\($0)\"" } ?? "null"
        let json = """
        {
          "user": {"id": "u1", "name": "Miguel"},
          "settings": {"mode": "\(mode)", "language": "\(language)", "hotkey": "\(hotkey)", "llmModel": \(llmModelJSON)},
          "dictionary": [],
          "server": {"version": "0.1.0", "model": "gpt-oss:120b", "concurrency": 1}
        }
        """
        return try! JSONDecoder().decode(MeResponse.self, from: Data(json.utf8))
    }

    // G1: a server-driven hotkey change goes through the callback, never a direct Preferences write.
    func testServerHotkeyChangeGoesThroughCallback() async {
        Preferences.hotkey = .fn
        let api = StubAPI()
        api.meResult = .success(meResponse(mode: "clean", language: "auto", hotkey: "rightOption"))
        let s = SyncService(api: api)
        var changes: [HotkeyChoice] = []
        s.onHotkeyChange = { changes.append($0) }

        await s.sync()

        // HotkeyChoice doesn't declare Equatable (out of this round's touched files) — compare rawValue.
        XCTAssertEqual(changes.map(\.rawValue), ["rightOption"])
        XCTAssertEqual(Preferences.hotkey.rawValue, "fn")   // SyncService itself never wrote it
    }

    // G3(a): push must not clear a server-set llmModel it doesn't know about.
    func testPushPreservesServerLlmModelOnMerge() async {
        let api = StubAPI()
        api.meResult = .success(meResponse(mode: "clean", language: "auto", hotkey: "fn", llmModel: "gpt-oss:120b"))
        let s = SyncService(api: api)
        await s.sync()

        await s.push(mode: "literal")

        XCTAssertEqual(api.puts.count, 1)
        XCTAssertEqual(api.puts.first?.llmModel, "gpt-oss:120b")
        XCTAssertEqual(api.puts.first?.mode, "literal")
    }

    // G3(b): pushing back the value sync() just wrote is a no-op — kills the VoiceApp .onChange echo.
    func testPushIsNoOpWhenUnchanged() async {
        let api = StubAPI()
        api.meResult = .success(meResponse(mode: "clean", language: "auto", hotkey: "fn"))
        let s = SyncService(api: api)
        await s.sync()

        await s.push(mode: "clean")

        XCTAssertEqual(api.puts.count, 0)
    }
}

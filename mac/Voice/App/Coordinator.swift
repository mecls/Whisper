import AppKit
import Combine
import Foundation
import os

private let log = Logger(subsystem: "co.miraside.voice", category: "coordinator")

/// Wires the reducer to the OS adapters. All on the main actor; adapters call back on main.
@MainActor
final class Coordinator: ObservableObject {
    // A2: single instance, started from AppDelegate.applicationDidFinishLaunching. No work in init.
    static let shared = Coordinator()
    private init() {}

    @Published private(set) var hud: HUDState = .hidden
    @Published var paused = false
    @Published private(set) var lastText: String?
    @Published private(set) var modelStatus = Strings.modelNotDownloaded

    private var machine = DictationMachine()
    private lazy var hotkey = HotkeyMonitor(choice: Preferences.hotkey)
    private let recorder = AudioRecorder()
    private let injector = TextInjector()
    private let panel = HUDPanel()
    private var levels: [Float] = []
    private var hudShowWork: DispatchWorkItem?
    private var hideWork: DispatchWorkItem?
    private var panelShown = false   // separates "update" (levels/state can change any time) from "reveal"

    var transcriber: Transcriber = WhisperKitTranscriber(modelId: Preferences.modelId)
    var refiner: Refiner = PassthroughRefiner()               // replaced in Task 8

    // C7: the backend client trio — a lazily-built API pointed at the configured server, reading the
    // bearer token read-only from the Keychain (account = the server URL string, C5); SyncService on
    // top of it; refiner is swapped from PassthroughRefiner to RefineService in start().
    private lazy var api = VoiceAPI(base: URL(string: Preferences.serverURL)!, tokenProvider: { Keychain.token(for: Preferences.serverURL) })
    lazy var sync = SyncService(api: api)

    func start() {
        if !Permissions.inputMonitoringGranted() || !Permissions.accessibilityGranted(prompt: false) {
            log.warning("permissions missing at launch — open \"\(Strings.setUpPermissions, privacy: .public)\" from the menu")
        }
        refiner = RefineService(api: api, outbox: Outbox())
        recorder.onLevel = { [weak self] l in self?.levels.append(l); if self?.hud == .listening { self?.render() } }
        recorder.onCapReached = { [weak self] in self?.send(.hotkeyUp) }
        hotkey.onAction = { [weak self] a in
            guard let self, !self.paused else { return }
            switch a {
            case .press: self.send(.hotkeyDown(FrontmostContext.current()))
            case .release: self.send(.hotkeyUp)
            case .cancel: self.send(.cancelRequested)
            }
        }
        try? recorder.prepare()
        _ = hotkey.start()
        sync.start()
        Task { await prepareModel() }
    }

    func setHotkey(_ c: HotkeyChoice) { Preferences.hotkey = c; hotkey.setChoice(c) }

    private func prepareModel() async {
        do {
            try await transcriber.prepare { [weak self] p in
                Task { @MainActor in self?.modelStatus = "\(Strings.modelLoading) \(Int(p * 100)) %"; self?.send(.modelProgress(p)) }
            }
            modelStatus = Strings.modelReady
            send(.modelReady)
        } catch {
            modelStatus = Strings.modelError(error.localizedDescription)
            log.error("model prepare failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    func send(_ e: MachineEvent) {
        for effect in machine.handle(e) { perform(effect) }
    }

    private func perform(_ effect: Effect) {
        switch effect {
        case .startRecording:
            levels = []
            hotkey.setListening(true)
            try? recorder.start()
            if Preferences.sounds { NSSound(named: "Tink")?.play() }
        case .stopRecording:
            hotkey.setListening(false)
            let (samples, ms) = recorder.stop()
            if Preferences.sounds { NSSound(named: "Pop")?.play() }
            send(.audioStopped(samples: samples, ms: ms, speech: EnergyGate.hasSpeech(samples)))
        case .discardRecording:
            hotkey.setListening(false)
            recorder.discard()
        case .transcribe(let id):
            guard let d = machine.queue.first(where: { $0.clientId == id }) else { return }
            let samples = d.samples
            let hint = TranscribeHint(language: Preferences.language == "auto" ? nil : Preferences.language, vocabulary: DictionaryCache.shared.terms)
            Task { [weak self] in
                do {
                    let t = try await self?.transcriber.transcribe(samples, hint: hint) { p in
                        Task { @MainActor in self?.showHUD(.transcribing(progress: p)) }
                    }
                    guard let t else { return }
                    self?.send(.transcribed(id, text: t.text, language: t.language, ms: t.durationMs))
                } catch {
                    log.error("transcribe failed: \(error.localizedDescription, privacy: .public)")
                    self?.send(.transcriptionFailed(id))
                }
            }
        case .refine(let id):
            guard let d = machine.queue.first(where: { $0.clientId == id }) else { return }
            Task { [weak self] in
                let r = await self?.refiner.refine(d, mode: Preferences.mode) ?? .rawFallback(.offline)
                self?.send(.refined(id, r))
            }
        case .insert(let id, let text):
            lastText = text
            injector.insert(text) { [weak self] result in
                let how: Injected = result == .clipboardOnly ? .clipboard : (self?.machine.queue.first { $0.clientId == id }?.cleaned == nil ? .raw : .cleaned)
                self?.send(.inserted(id, how))
                if result == .clipboardOnly { self?.showHUD(.message(Strings.secureField)) }
            }
        case .reportInjected(let id, let how):
            Task { await refiner.reportInjected(clientId: id, injected: how) }
        case .hud(let state):
            showHUD(state)
        }
    }

    private func showHUD(_ state: HUDState) {
        hud = state
        hudShowWork?.cancel(); hideWork?.cancel()
        switch state {
        case .hidden:
            panelShown = false
            panel.hide()
        case .listening:
            // 150 ms delay: Fn+arrow combos cancel before the HUD ever appears. Nothing between
            // now and the timer firing (onLevel's render() calls included) may reveal the panel.
            let w = DispatchWorkItem { [weak self] in
                self?.panelShown = true
                self?.render()
            }
            hudShowWork = w
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.15, execute: w)
        case .done, .message:
            panelShown = true
            render()
            let w = DispatchWorkItem { [weak self] in
                self?.panelShown = false
                self?.panel.hide()
            }
            hideWork = w
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.2, execute: w)
        default:
            panelShown = true
            render()
        }
    }

    /// Updates the panel's content only once it has been revealed (see `showHUD`'s `.listening`
    /// case) — called on every level tick, but a no-op until `panelShown` is set.
    private func render() {
        guard panelShown else { return }
        panel.show(hud, levels: levels)
    }
}

// Task 6 stub. Superseded by WhisperKitTranscriber (Task 7) as the Coordinator's default, but kept
// for tests/dev that want a deterministic, instant Transcriber.
struct FixedTextTranscriber: Transcriber {
    var isReady: Bool { true }
    func prepare(progress: @escaping (Double) -> Void) async throws {}
    func transcribe(_ samples: [Float], hint: TranscribeHint, progress: ((Double) -> Void)?) async throws -> Transcript {
        Transcript(text: "hello", language: "en", durationMs: 0)
    }
}

struct PassthroughRefiner: Refiner {
    func refine(_ d: Dictation, mode: String) async -> RefineResult { .literal }
    func reportInjected(clientId: UUID, injected: Injected) async {}
}

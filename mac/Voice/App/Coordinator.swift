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
    // H2/H3: the model actually loaded (or being loaded) into `transcriber` — distinct from
    // `Preferences.modelId`/the Settings picker, which may point at a different, not-yet-loaded id.
    @Published private(set) var activeModelId = Preferences.modelId
    /// True while a hands-free session is running. Drives the bar's indicator and the menu bar
    /// icon; must be cleared by every route a session can end, or a stale indicator claims a
    /// microphone is live when it is not.
    @Published private(set) var isLatched = false

    private var machine = DictationMachine()
    private lazy var hotkey = HotkeyMonitor(choice: Preferences.hotkey)
    private var lastPrewarm: Date?
    // Rule 15's clock. `pendingRelease` is stamped at `.stopRecording` and claimed by the next
    // `.transcribe`; `releaseAt` then holds it per dictation until the paste closes it. Entries are
    // removed on use, and a dictation that never pastes (cancelled, failed) simply leaves a stale
    // Date that the next dictation with that id would overwrite — the map is bounded by the queue.
    private var pendingRelease: Date?
    private var releaseAt: [UUID: Date] = [:]
    /// The double-tap gesture rules. Pure and clock-free; this owns the clock on its behalf, the
    /// same way it owns the release→paste clock above.
    private var tapLatch = TapLatch()
    private var latchWindowWork: DispatchWorkItem?
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
    // D3/Task 9: internal (not private) so the Settings window's Dictionary tab can call
    // listTerms/addTerm/deleteTerm directly.
    lazy var api = VoiceAPI(base: URL(string: Preferences.serverURL)!, tokenProvider: { Keychain.token(for: Preferences.serverURL) })
    lazy var sync = SyncService(api: api)
    /// The Insights window's model. Lazy and owned here so the window can be closed and reopened
    /// without losing the numbers already on screen, or re-reading the cache from disk each time.
    lazy var insights = InsightsModel(api: api)

    func start() {
        if !Permissions.inputMonitoringGranted() || !Permissions.accessibilityGranted(prompt: false) {
            log.warning("permissions missing at launch — open \"\(Strings.setUpPermissions, privacy: .public)\" from the menu")
        }
        activeModelId = Preferences.modelId
        refiner = RefineService(api: api, outbox: Outbox())
        recorder.onLevel = { [weak self] l in self?.levels.append(l); if self?.hud == .listening { self?.render() } }
        recorder.onCapReached = { [weak self] in self?.capReached() }
        hotkey.onAction = { [weak self] a in
            guard let self, !self.paused else { return }
            self.handleHotkey(a, at: Date())
        }
        try? recorder.prepare()
        _ = hotkey.start()
        // G1: a server-driven hotkey change must go through setHotkey (owns writing Preferences.hotkey
        // AND re-arming the hotkey monitor) — wired before sync.start() so the very first sync can use it.
        sync.onHotkeyChange = { [weak self] c in self?.setHotkey(c) }
        sync.start()
        Task { await prepareModel() }
    }

    func setHotkey(_ c: HotkeyChoice) { Preferences.hotkey = c; hotkey.setChoice(c) }

    // D5: the Settings window's Model tab "Download"/"Delete" actions call this to pick up whatever
    // `Preferences.modelId` the picker just selected — swap the transcriber, reset the progress UI,
    // then re-run the same prepare path `start()` uses.
    func reloadModel() {
        activeModelId = Preferences.modelId
        transcriber = WhisperKitTranscriber(modelId: Preferences.modelId)
        send(.modelProgress(0))
        Task { [weak self] in await self?.prepareModel() }
    }

    private func prepareModel() async {
        do {
            try await transcriber.prepare { [weak self] p in
                Task { @MainActor in self?.modelStatus = "\(Strings.modelLoading) \(Int(p * 100)) %"; self?.send(.modelProgress(p)) }
            }
            modelStatus = Strings.modelReady
            send(.modelReady)
        } catch {
            // H2/H3: the machine stays in `.modelLoading` until a reload succeeds — Settings' "Reload
            // model" button (always enabled) is the recovery path for this state.
            modelStatus = Strings.modelError(error.localizedDescription)
            log.error("model prepare failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    /// Turns a key action into machine events, via the gesture rules.
    ///
    /// `Date()` is read here and nowhere below: `TapLatch` and `DictationMachine` both stay pure,
    /// which is what keeps their tests free of sleeps.
    private func handleHotkey(_ action: HotkeyAction, at now: Date) {
        for outcome in tapLatch.handle(action, at: now) { apply(outcome) }
    }

    private func apply(_ outcome: TapLatch.Outcome) {
        switch outcome {
        case .startDictation:
            send(.hotkeyDown(FrontmostContext.current()))

        case .holdOpen:
            // The key came up too quickly to be a dictation. Keep recording — the reducer would
            // have discarded this as too short anyway — and wait to see if a second tap arrives.
            scheduleLatchWindow()

        case .latch:
            cancelLatchWindow()
            isLatched = true
            hotkey.setLatched(true)

        case .endSession:
            cancelLatchWindow()
            clearLatch()
            send(.hotkeyUp)

        case .abandon:
            cancelLatchWindow()
            clearLatch()
            // Cancel first, then show. `.cancelRequested` returns `.hud(.hidden)`, so showing the
            // message first would have it wiped by the cancel in the same turn — the user would see
            // nothing at all and a stray tap would look like the app ignoring them.
            send(.cancelRequested)
            showHUD(.message(Strings.tapTooShort))
        }
    }

    private func scheduleLatchWindow() {
        cancelLatchWindow()
        // DispatchWorkItem rather than Timer, following showHUD's existing pattern: it must be
        // cancellable the instant a second tap lands, or it would abandon a session that just
        // started.
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            for outcome in self.tapLatch.windowExpired(at: Date()) { self.apply(outcome) }
        }
        latchWindowWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + Double(TapLatch.windowMs) / 1000, execute: work)
    }

    private func cancelLatchWindow() {
        latchWindowWork?.cancel()
        latchWindowWork = nil
    }

    /// Every route a session can end funnels through here. Missing one leaves `isLatched` true over
    /// a finished session, which is worse than never showing it: the indicator's whole job is to say
    /// a microphone is open.
    private func clearLatch() {
        isLatched = false
        hotkey.setLatched(false)
    }

    /// The 90 s ceiling. Reachable for the first time now — nobody holds a key for 90 seconds, but
    /// a hands-free session left running gets there. It must read as a limit, not a crash.
    private func capReached() {
        cancelLatchWindow()
        let wasLatched = isLatched
        clearLatch()
        tapLatch.reset()
        send(.hotkeyUp)
        if wasLatched { showHUD(.message(Strings.latchCapReached)) }
    }

    func send(_ e: MachineEvent) {
        for effect in machine.handle(e) { perform(effect) }
    }

    private func perform(_ effect: Effect) {
        switch effect {
        case .startRecording:
            levels = []
            hotkey.setListening(true)
            do {
                try recorder.start()
            } catch {
                // Previously `try?`. A microphone that cannot be opened then produced a HUD that
                // said "Listening" over an engine recording nothing, which is indistinguishable
                // from the app working right up until no text appears — and gives the user nothing
                // to act on. Say so and end the dictation instead.
                log.error("could not start recording: \(String(describing: error), privacy: .public)")
                cancelLatchWindow()
                clearLatch()
                tapLatch.reset()
                // Cancel before showing: `.cancelRequested` returns `.hud(.hidden)`, which would
                // wipe the message if it were shown first.
                send(.cancelRequested)
                showHUD(.message(Strings.noMicrophone))
                return
            }
            prewarmConnection()   // rule 19: strictly after the microphone is open
            if Preferences.sounds { NSSound(named: "Tink")?.play() }
        case .stopRecording:
            // Rule 15: the release→paste clock starts here, before the audio engine is torn
            // down, because stopping the recorder is part of the latency the user feels. It is held
            // here rather than on the Dictation so the reducer stays free of wall-clock reads.
            pendingRelease = Date()
            hotkey.setListening(false)
            let (samples, ms) = recorder.stop()
            if Preferences.sounds { NSSound(named: "Pop")?.play() }
            send(.audioStopped(samples: samples, ms: ms, speech: EnergyGate.hasSpeech(samples)))
        case .discardRecording:
            hotkey.setListening(false)
            recorder.discard()
        case .transcribe(let id):
            // `.transcribe` is emitted synchronously from `.audioStopped`, which the `.stopRecording`
            // effect above sends in the same turn — so this is the first point at which the release
            // we just stamped has an id to belong to. Only dictations that got this far are
            // measurable, which is exactly the set worth measuring.
            if let r = pendingRelease { releaseAt[id] = r; pendingRelease = nil }
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
            // The app that was frontmost when the hotkey went down, not whatever is frontmost now:
            // the paste is aimed at where the user was typing. When that app is Voice itself, the
            // injector routes to the clipboard instead of typing into our own window.
            let target = machine.queue.first { $0.clientId == id }?.app?.bundleId
            injector.insert(text, targetBundleId: target) { [weak self] result in
                let how: Injected = result == .clipboardOnly ? .clipboard : (self?.machine.queue.first { $0.clientId == id }?.cleaned == nil ? .raw : .cleaned)
                self?.send(.inserted(id, how))
                if result == .clipboardOnly { self?.showHUD(.message(Strings.secureField)) }
            }
        case .reportInjected(let id, let how):
            let totalMs = releaseAt.removeValue(forKey: id).map { Int(Date().timeIntervalSince($0) * 1000) }
            Task { await refiner.reportInjected(clientId: id, injected: how, totalMs: totalMs) }
        case .hud(let state):
            showHUD(state)
        }
    }

    /// Rules 19-21, adapted for §1a. The spec gated pre-warming on "no server call expected",
    /// which assumed on-device cleanup was the default; with the local engine on hold the server is
    /// the only cleanup engine, so a call is always expected. Warming on every key press would
    /// still be pointless chatter — URLSession keeps the pooled connection alive between
    /// dictations — so one warm a minute keeps it fresh without a steady trickle to the VPS.
    private func prewarmConnection() {
        if let last = lastPrewarm, Date().timeIntervalSince(last) < 60 { return }
        lastPrewarm = Date()
        api.prewarm()
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
    func reportInjected(clientId: UUID, injected: Injected, totalMs: Int?) async {}
}

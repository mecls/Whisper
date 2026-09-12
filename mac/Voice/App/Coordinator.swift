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
    /// Live transcription. Off unless `Preferences.liveTranscription` is on — the accuracy gate that
    /// would justify defaulting it on could not be measured (see the build's decisions log).
    let streaming = StreamingTranscriber()
    private lazy var streamProcessor = VoiceAudioProcessor(recorder: recorder)
    /// How many confirmed segments produced each dictation's text. 1 means no chunk boundary, which
    /// is what lets the skip gate leave it uncleaned.
    private var streamedSegments: [UUID: Int] = [:]
    private var latchWindowWork: DispatchWorkItem?
    private let recorder = AudioRecorder()
    private let injector = TextInjector()
    private let panel = HUDPanel()
    private var levels: [Float] = []
    private var hideWork: DispatchWorkItem?

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
        panel.onMicTap = { [weak self] in self?.toggleLatchedFromBar() }
        panel.applyVisibility()      // the bar is on screen from launch, not from the first dictation
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
            render()

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
        render()
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
        purgeFinishedDictations()
    }

    /// Drops per-dictation bookkeeping for ids the reducer has already finished with.
    ///
    /// `releaseAt` and `streamedSegments` are cleared explicitly in `.reportInjected`, which is the
    /// path a successful dictation takes. A cancelled one, or one whose transcription failed, never
    /// gets there and used to leave its entry behind forever. The queue is the authority on what is
    /// still in flight, so sweeping against it catches every exit path including ones added later —
    /// which matters more than the few bytes, because the next person to add an early-exit will not
    /// think to clean up two dictionaries they did not know existed.
    private func purgeFinishedDictations() {
        guard !releaseAt.isEmpty || !streamedSegments.isEmpty else { return }
        let live = Set(machine.queue.map(\.clientId))
        releaseAt = releaseAt.filter { live.contains($0.key) }
        streamedSegments = streamedSegments.filter { live.contains($0.key) }
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
            startStreamingIfEnabled()
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
            Task { _ = await streaming.finish() }   // never leave a stream running past its dictation
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
                // The streamed transcript is already finished — that is the entire point, and why
                // release→paste no longer contains an ASR pass. `asrMs` is 0 for these because no
                // transcription happened after the key came up; the work was done while speaking.
                if let streamed = await self?.streaming.finish() {
                    // The stream stops at the last segment Whisper produced, and
                    // `AudioStreamTranscriber` runs no final pass when it is told to stop — it only
                    // transcribes once a full second of new audio has arrived, and whatever came in
                    // after the last pass started is simply never transcribed. This was assumed to
                    // be a sub-second rounding error. Measured, it was 4396 ms of a 15295 ms
                    // dictation: 29% of what the user said, silently missing from the paste.
                    //
                    // So the tail gets one pass of its own. It is over the leftover audio only, not
                    // the whole recording, which is what keeps release→paste short — the streamed
                    // part is already transcribed and is not redone.
                    let covered = self?.streaming.coveredMs ?? 0
                    var text = streamed.text
                    var asrMs = 0
                    if let tail = Self.tail(of: samples, afterMs: covered, totalMs: d.audioMs) {
                        log.info("transcribing stream tail: \(d.audioMs - covered, privacy: .public) ms of \(d.audioMs, privacy: .public) ms")
                        if let t = try? await self?.transcriber.transcribe(tail, hint: hint, progress: nil) {
                            let extra = t.text.trimmingCharacters(in: .whitespacesAndNewlines)
                            if !extra.isEmpty { text += " " + extra }
                            asrMs = t.durationMs
                        }
                    }
                    self?.streamedSegments[id] = streamed.segments
                    self?.send(.transcribed(id, text: text,
                                            language: hint.language ?? "auto", ms: asrMs))
                    return
                }
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
            guard var d = machine.queue.first(where: { $0.clientId == id }) else { return }
            // Carried on the Coordinator's own copy rather than through the reducer: the segment
            // count is an artefact of how the text was produced, not part of the dictation's state
            // machine, and threading it through MachineEvent would widen a deliberately small
            // event set for one consumer.
            d.streamedSegments = streamedSegments[id] ?? 1
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
            streamedSegments.removeValue(forKey: id)
            let totalMs = releaseAt.removeValue(forKey: id).map { Int(Date().timeIntervalSince($0) * 1000) }
            Task { await refiner.reportInjected(clientId: id, injected: how, totalMs: totalMs) }
        case .hud(let state):
            showHUD(state)
        }
    }

    private func startStreamingIfEnabled() {
        guard Preferences.liveTranscription,
              let pipe = (transcriber as? WhisperKitTranscriber)?.whisperKit else { return }
        let hint = TranscribeHint(language: Preferences.language == "auto" ? nil : Preferences.language,
                                  vocabulary: DictionaryCache.shared.terms)
        streaming.start(pipe: pipe, processor: streamProcessor, hint: hint)
    }

    /// Text to show in the bar while speaking: settled text plus the current hypothesis. Behind
    /// `showTextInHUD`, which governs every appearance of transcript text on screen — on a bar that
    /// never hides, that single switch is the whole privacy story.
    private var liveBarText: String? {
        guard Preferences.liveTranscription, Preferences.showTextInHUD else { return nil }
        let combined = (streaming.confirmedText + " " + streaming.unconfirmedText)
            .trimmingCharacters(in: .whitespaces)
        return combined.isEmpty ? nil : combined
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
        hideWork?.cancel()
        render()
        // The bar no longer appears and disappears with the dictation, so `.done` and `.message`
        // revert its *content* to idle rather than hiding the panel. The old 150 ms reveal delay
        // for `.listening` is gone with the auto-hide it existed for: there is nothing to reveal.
        if case .done = state { scheduleReturnToIdle() }
        if case .message = state { scheduleReturnToIdle() }
    }

    private func scheduleReturnToIdle() {
        let w = DispatchWorkItem { [weak self] in self?.showHUD(.hidden) }
        hideWork = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2, execute: w)
    }

    /// Called from the menu's "Show bar" toggle.
    func refreshBarVisibility() { panel.applyVisibility() }

    /// The bar's mic button. Mouse-started sessions are always hands-free — there is no mouse
    /// equivalent of holding a key down.
    private func toggleLatchedFromBar() {
        guard !paused else { return }
        if isLatched {
            cancelLatchWindow()
            clearLatch()
            tapLatch.reset()
            send(.hotkeyUp)
            return
        }
        send(.hotkeyDown(FrontmostContext.current()))
        // Only latch if a recording actually started. `.hotkeyDown` refuses while the model is
        // still loading, and latching over a dictation that never began would leave the indicator
        // claiming a live microphone.
        guard hud == .listening else { return }
        tapLatch.forceLatched()
        isLatched = true
        hotkey.setLatched(true)
        render()
    }

    private func render() {
        panel.update(hud, levels: levels, latched: isLatched, liveText: liveBarText)
    }

    // MARK: - Streamed tail

    /// The audio after `afterMs` that the stream never transcribed, or nil when there is nothing
    /// there worth a pass.
    ///
    /// Two guards, and removing either can only make the result worse. A gap under `minimumTailMs`
    /// is not a word, and handing Whisper a very short clip invites a hallucinated one. And a gap
    /// that is *silence* — the ordinary case of stopping talking a beat before letting go of the
    /// key — is exactly what Whisper hallucinates on, so `EnergyGate`, the same gate that decides
    /// whether a whole dictation contains speech, decides this too.
    ///
    /// The offset comes from the sample count rather than a hard-coded rate, so it cannot drift
    /// away from whatever the recorder is actually producing.
    static func tail(of samples: [Float], afterMs: Int, totalMs: Int) -> [Float]? {
        guard totalMs > 0, !samples.isEmpty, afterMs >= 0 else { return nil }
        guard totalMs - afterMs >= minimumTailMs else { return nil }
        let start = Int((Double(afterMs) / Double(totalMs)) * Double(samples.count))
        guard start >= 0, start < samples.count else { return nil }
        let tail = Array(samples[start...])
        guard EnergyGate.hasSpeech(tail) else { return nil }
        return tail
    }

    /// Below this, a gap is a pause or a rounding error rather than a word.
    static let minimumTailMs = 400

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

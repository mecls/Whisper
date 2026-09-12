import AVFoundation
import Foundation
import os

private let log = Logger(subsystem: "co.miraside.voice", category: "audio")

enum AudioRecorderError: Error, Equatable {
    /// The input node reports no usable format — no microphone, or the current one just went away.
    case noInputDevice
}

/// AVAudioEngine input → 16 kHz mono Float32 → RingBuffer. Nothing is written to disk.
final class AudioRecorder {
    static let sampleRate = 16000
    static let maxSeconds = 90

    private let engine = AVAudioEngine()
    private var converter: AVAudioConverter?
    private let buffer = RingBuffer(capacity: sampleRate * maxSeconds)
    private let targetFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: Double(sampleRate), channels: 1, interleaved: false)!
    private var startedAt: Date?
    private var idleStopTimer: Timer?
    private var configObserver: NSObjectProtocol?
    var onLevel: ((Float) -> Void)?
    var onCapReached: (() -> Void)?
    private(set) var isCapturing = false

    /// The format buffers are actually arriving in. Diagnostics only: when a dictation comes back
    /// empty the first question is which device the engine was really on, and that was previously
    /// unanswerable without attaching a debugger.
    private(set) var captureFormat: AVAudioFormat?

    var isRunning: Bool { engine.isRunning }

    /// Everything captured so far this session, without consuming it. For the streaming adapter.
    var capturedSamples: [Float] { buffer.snapshot() }

    /// Recent per-buffer RMS, newest last — the same values the bar's waveform draws. Streaming's
    /// VAD reads this rather than measuring energy a second time: two independent measurements
    /// would drift, and the waveform would then disagree with the gate deciding whether the user
    /// is speaking.
    private(set) var recentEnergy: [Float] = []

    deinit {
        if let configObserver { NotificationCenter.default.removeObserver(configObserver) }
    }

    /// Keeps the engine prepared (and, after a dictation, running for ~20 s) so the first syllable is not clipped.
    func prepare() throws {
        // Registered before the first tap install, deliberately: if there is no input device at
        // launch the install below throws, and the notification is then the only thing that will
        // tell us a device has appeared. Registering afterwards would leave the app permanently
        // deaf to a microphone plugged in one second later.
        observeConfigurationChanges()
        try installTap()
    }

    /// Re-installs the input tap for whatever device is current.
    ///
    /// Only the tap lives here. The converter is built inside `consume` from the format that
    /// actually arrives, because that is the only format guaranteed to be true — during a route
    /// change the node advertises one thing and delivers another.
    private func installTap() throws {
        let input = engine.inputNode

        // Stop before touching the input chain. Reconfiguring it on a live engine is what raises
        // `Failed to initialize active nodes in input chain! err = -10868`
        // (kAudioUnitErr_FormatNotSupported) — an Objective-C exception, so Swift cannot catch it
        // and the process aborts.
        if engine.isRunning { engine.stop() }

        // `inputFormat(forBus:)`, not `outputFormat(forBus:)`. AVAudioEngine asserts
        // `format.sampleRate == inputHWFormat.sampleRate` when installing the tap, and it is the
        // *hardware* format it compares against. The two agree while the device is stable and
        // diverge for a moment during a route change — which is precisely when this runs, so the
        // stale one aborted the process every time the input device changed.
        let inFormat = input.inputFormat(forBus: 0)
        // A device mid-handover, or one that has just disappeared, reports 0 channels / 0 Hz.
        guard inFormat.channelCount > 0, inFormat.sampleRate > 0 else {
            input.removeTap(onBus: 0)
            log.error("no usable audio input (format \(inFormat, privacy: .public))")
            throw AudioRecorderError.noInputDevice
        }

        input.removeTap(onBus: 0)
        // The format is named explicitly rather than passed as nil. `nil` asks the engine to use
        // the node's own format, which sounds safer and is not: on a Bluetooth input it fails to
        // initialise the input chain at launch with the same -10868. Reading the format and
        // handing it straight back is what this engine accepts.
        input.installTap(onBus: 0, bufferSize: 1024, format: inFormat) { [weak self] pcm, _ in
            self?.consume(pcm)
        }
        engine.prepare()
        captureFormat = inFormat
        log.info("audio tap installed: \(inFormat.sampleRate, privacy: .public) Hz, \(inFormat.channelCount, privacy: .public) ch")
    }

    /// Rebuilds the capture chain whenever the engine's configuration changes.
    ///
    /// This is what makes AirPods work. Connecting them makes them the default input, which changes
    /// the input format under a tap and converter built for the previous device. Without this the
    /// engine keeps delivering buffers and the converter keeps "succeeding" on them, producing
    /// near-silence: loud enough to clear the energy gate, empty enough that Whisper returns
    /// nothing, so a dictation reaches the server carrying zero words and the app looks like it has
    /// stopped hearing you for no visible reason.
    private func observeConfigurationChanges() {
        guard configObserver == nil else { return }
        configObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange,
            object: engine,
            queue: nil
        ) { [weak self] _ in
            // Posted on an arbitrary thread, and posted *during* the route change rather than
            // after it. Rebuilding immediately catches CoreAudio mid-handover, when the input
            // node still reports the old device's format and initialising the chain with it
            // aborts the process. The delay lets the new device settle first; it costs nothing,
            // because the engine is already stopped by the system at this point.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
                self?.rebuildForCurrentDevice(reason: "configuration change")
            }
        }
    }

    private func rebuildForCurrentDevice(reason: String) {
        dispatchPrecondition(condition: .onQueue(.main))

        // The system stops the engine when the configuration changes. Remember whether we owe it a
        // restart before touching anything.
        let shouldRun = isCapturing || engine.isRunning

        do {
            try installTap()
        } catch {
            // Nothing to listen to right now. `isCapturing` stays as it was: if this happened
            // mid-dictation the user still gets whatever was captured before the device vanished,
            // rather than an error they cannot act on.
            log.error("could not rebuild audio input after \(reason, privacy: .public): \(String(describing: error), privacy: .public)")
            return
        }

        if shouldRun && !engine.isRunning {
            do {
                try engine.start()
            } catch {
                log.error("could not restart audio engine after \(reason, privacy: .public): \(error.localizedDescription, privacy: .public)")
                return
            }
        }
        // Samples already in the ring buffer were converted to `targetFormat` before being stored,
        // so they stay valid across the change and a dictation in progress simply continues.
        log.info("audio input rebuilt after \(reason, privacy: .public)")
    }

    func start() throws {
        dispatchPrecondition(condition: .onQueue(.main))   // A3: idle-stop Timer needs the main run loop
        idleStopTimer?.invalidate()
        // The device may have changed while the engine sat idle between dictations — and if the
        // engine was paused, a configuration change may not have reached us at all. Cheap to check,
        // and it is the difference between the first dictation after plugging in AirPods working
        // and silently recording nothing.
        let live = engine.inputNode.inputFormat(forBus: 0)
        guard live.channelCount > 0, live.sampleRate > 0 else { throw AudioRecorderError.noInputDevice }
        if !Self.formatsMatch(live, captureFormat) {
            rebuildForCurrentDevice(reason: "input changed while idle")
        }
        if !engine.isRunning { try engine.start() }
        _ = buffer.drain()
        recentEnergy = []
        startedAt = Date()
        isCapturing = true
    }

    func stop() -> (samples: [Float], ms: Int) {
        dispatchPrecondition(condition: .onQueue(.main))   // A3: idle-stop Timer needs the main run loop
        isCapturing = false
        let ms = Int((Date().timeIntervalSince(startedAt ?? Date())) * 1000)
        scheduleIdleStop()
        return (buffer.drain(), ms)
    }

    func discard() {
        dispatchPrecondition(condition: .onQueue(.main))   // A3: idle-stop Timer needs the main run loop
        isCapturing = false
        _ = buffer.drain()
        scheduleIdleStop()
    }

    /// Whether a buffer in `incoming` can be fed to a converter built for `expected`.
    ///
    /// Sample rate and channel count are what the converter is built around; a mismatch in either
    /// means the conversion is meaningless even when it reports success.
    static func formatsMatch(_ incoming: AVAudioFormat?, _ expected: AVAudioFormat?) -> Bool {
        guard let incoming, let expected else { return false }
        return incoming.sampleRate == expected.sampleRate && incoming.channelCount == expected.channelCount
    }

    private func scheduleIdleStop() {
        idleStopTimer?.invalidate()
        idleStopTimer = Timer.scheduledTimer(withTimeInterval: 20, repeats: false) { [weak self] _ in
            guard let self, !self.isCapturing else { return }
            self.engine.pause()
        }
    }

    private func consume(_ pcm: AVAudioPCMBuffer) {
        guard isCapturing else { return }

        // The converter is built from the format that actually arrived, not from the one the
        // input node advertised when the tap was installed. During a device change those two
        // disagree, and converting with the stale one does not fail loudly — it yields
        // near-silence: enough signal to clear the energy gate, nothing for the transcriber. That
        // is what made connecting AirPods mid-session look like the app had gone deaf.
        //
        // `converter` is touched only here, on the tap's own serial thread, so it needs no lock.
        if !Self.formatsMatch(pcm.format, converter?.inputFormat) {
            guard let rebuilt = AVAudioConverter(from: pcm.format, to: targetFormat) else { return }
            converter = rebuilt
            let arrived = pcm.format
            DispatchQueue.main.async { [weak self] in
                self?.captureFormat = arrived
                log.info("capture format now \(arrived.sampleRate, privacy: .public) Hz, \(arrived.channelCount, privacy: .public) ch")
            }
        }
        guard let converter else { return }

        let ratio = targetFormat.sampleRate / pcm.format.sampleRate
        let capacity = AVAudioFrameCount(Double(pcm.frameLength) * ratio) + 16
        guard let out = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: capacity) else { return }
        var consumed = false
        var error: NSError?
        converter.convert(to: out, error: &error) { _, status in
            if consumed { status.pointee = .noDataNow; return nil }
            consumed = true; status.pointee = .haveData; return pcm
        }
        guard error == nil, let ch = out.floatChannelData?[0] else { return }
        let ptr = UnsafeBufferPointer(start: ch, count: Int(out.frameLength))
        buffer.write(ptr)
        let level = EnergyGate.rms(ArraySlice(ptr))
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.recentEnergy.append(level)
            // Bounded: this is a rolling view for the waveform and the VAD, not a history.
            if self.recentEnergy.count > 256 { self.recentEnergy.removeFirst(self.recentEnergy.count - 256) }
            self.onLevel?(level)
        }
        if buffer.isFull { DispatchQueue.main.async { [weak self] in self?.onCapReached?() } }
    }
}

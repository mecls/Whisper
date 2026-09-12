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
    private var rebuildQueued = false
    var onLevel: ((Float) -> Void)?
    var onCapReached: (() -> Void)?
    private(set) var isCapturing = false

    /// The format the tap and converter are currently built for. Exposed for diagnostics: when a
    /// dictation comes back empty, the first question is which device the engine was actually on.
    private(set) var inputFormat: AVAudioFormat?

    var isRunning: Bool { engine.isRunning }

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

    /// Reads the *current* input format and rebuilds everything that depends on it.
    ///
    /// Every device change has to come through here. An `AVAudioConverter` is built for one
    /// specific input format and a tap is installed with one specific format, so neither survives
    /// the input device being swapped underneath them.
    private func installTap() throws {
        let input = engine.inputNode
        let inFormat = input.outputFormat(forBus: 0)
        // A device that has just disappeared reports 0 channels / 0 Hz, and installing a tap with
        // that format throws inside AVFoundation. Fail here instead, with a reason.
        guard inFormat.channelCount > 0, inFormat.sampleRate > 0 else {
            inputFormat = nil
            converter = nil
            input.removeTap(onBus: 0)
            log.error("no usable audio input (format \(inFormat, privacy: .public))")
            throw AudioRecorderError.noInputDevice
        }
        converter = AVAudioConverter(from: inFormat, to: targetFormat)
        inputFormat = inFormat
        input.removeTap(onBus: 0)
        input.installTap(onBus: 0, bufferSize: 1024, format: inFormat) { [weak self] pcm, _ in
            self?.consume(pcm)
        }
        engine.prepare()
        log.info("audio input ready: \(inFormat.sampleRate, privacy: .public) Hz, \(inFormat.channelCount, privacy: .public) ch")
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
            // Posted on an arbitrary thread; every field it touches belongs to main.
            DispatchQueue.main.async { self?.rebuildForCurrentDevice(reason: "configuration change") }
        }
    }

    private func rebuildForCurrentDevice(reason: String) {
        dispatchPrecondition(condition: .onQueue(.main))
        rebuildQueued = false

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
        if converter == nil || !Self.formatsMatch(engine.inputNode.outputFormat(forBus: 0), inputFormat) {
            rebuildForCurrentDevice(reason: "input changed while idle")
        }
        guard converter != nil else { throw AudioRecorderError.noInputDevice }
        if !engine.isRunning { try engine.start() }
        _ = buffer.drain()
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
        guard isCapturing, let converter else { return }

        // A buffer from the previous device can still arrive between the route changing and the
        // notification being handled. Feeding it to a converter built for a different format does
        // not fail loudly — it yields near-silence, which is the worst possible outcome: enough
        // signal to pass the energy gate, nothing for the transcriber. Drop it and rebuild.
        guard Self.formatsMatch(pcm.format, converter.inputFormat) else {
            if !rebuildQueued {
                rebuildQueued = true
                DispatchQueue.main.async { [weak self] in self?.rebuildForCurrentDevice(reason: "format mismatch on capture") }
            }
            return
        }

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
        DispatchQueue.main.async { [weak self] in self?.onLevel?(level) }
        if buffer.isFull { DispatchQueue.main.async { [weak self] in self?.onCapReached?() } }
    }
}

import AVFoundation
import Foundation

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
    var onLevel: ((Float) -> Void)?
    var onCapReached: (() -> Void)?
    private(set) var isCapturing = false

    var isRunning: Bool { engine.isRunning }

    /// Keeps the engine prepared (and, after a dictation, running for ~20 s) so the first syllable is not clipped.
    func prepare() throws {
        let input = engine.inputNode
        let inFormat = input.outputFormat(forBus: 0)
        converter = AVAudioConverter(from: inFormat, to: targetFormat)
        input.removeTap(onBus: 0)
        input.installTap(onBus: 0, bufferSize: 1024, format: inFormat) { [weak self] pcm, _ in
            self?.consume(pcm)
        }
        engine.prepare()
    }

    func start() throws {
        idleStopTimer?.invalidate()
        if !engine.isRunning { try engine.start() }
        _ = buffer.drain()
        startedAt = Date()
        isCapturing = true
    }

    func stop() -> (samples: [Float], ms: Int) {
        isCapturing = false
        let ms = Int((Date().timeIntervalSince(startedAt ?? Date())) * 1000)
        scheduleIdleStop()
        return (buffer.drain(), ms)
    }

    func discard() {
        isCapturing = false
        _ = buffer.drain()
        scheduleIdleStop()
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

import AVFoundation
import Foundation

/// Test-only helper: loads a whole audio file and converts it to 16 kHz mono Float32, the same
/// way `AudioRecorder.consume` converts live microphone buffers (AVAudioConverter, single pass).
enum AudioFile {
    enum LoadError: Error { case converterUnavailable, bufferUnavailable, conversionFailed }

    static func load16k(_ url: URL) throws -> [Float] {
        let file = try AVAudioFile(forReading: url)
        let inFormat = file.processingFormat
        let targetFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16000, channels: 1, interleaved: false)!

        guard let inBuffer = AVAudioPCMBuffer(pcmFormat: inFormat, frameCapacity: AVAudioFrameCount(file.length)) else {
            throw LoadError.bufferUnavailable
        }
        try file.read(into: inBuffer)

        guard let converter = AVAudioConverter(from: inFormat, to: targetFormat) else { throw LoadError.converterUnavailable }
        let ratio = targetFormat.sampleRate / inFormat.sampleRate
        let capacity = AVAudioFrameCount(Double(inBuffer.frameLength) * ratio) + 16
        guard let outBuffer = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: capacity) else {
            throw LoadError.bufferUnavailable
        }

        var consumed = false
        var convError: NSError?
        converter.convert(to: outBuffer, error: &convError) { _, status in
            if consumed { status.pointee = .noDataNow; return nil }
            consumed = true; status.pointee = .haveData; return inBuffer
        }
        if let convError { throw convError }
        guard let ch = outBuffer.floatChannelData?[0] else { throw LoadError.conversionFailed }
        return Array(UnsafeBufferPointer(start: ch, count: Int(outBuffer.frameLength)))
    }
}

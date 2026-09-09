import Foundation

/// Energy gate: 95th percentile of 20 ms frame RMS must clear the threshold (~-40 dBFS).
/// Rejects silence (and Whisper's silence hallucinations) and single clicks.
enum EnergyGate {
    static func rms(_ xs: ArraySlice<Float>) -> Float {
        guard !xs.isEmpty else { return 0 }
        var acc: Float = 0
        for x in xs { acc += x * x }
        return (acc / Float(xs.count)).squareRoot()
    }

    static func hasSpeech(_ xs: [Float], sampleRate: Int = 16000, threshold: Float = 0.01) -> Bool {
        let frame = sampleRate / 50
        guard xs.count >= frame else { return false }
        var levels: [Float] = []
        levels.reserveCapacity(xs.count / frame)
        var i = 0
        while i + frame <= xs.count { levels.append(rms(xs[i..<(i + frame)])); i += frame }
        levels.sort()
        let p95 = levels[min(levels.count - 1, Int(Double(levels.count) * 0.95))]
        return p95 >= threshold
    }
}

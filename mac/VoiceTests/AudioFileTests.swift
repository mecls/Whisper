import AVFoundation
import XCTest
@testable import Voice

/// Always-on (no model, no VOICE_ASR_TESTS gate): catches silent truncation in `AudioFile.load16k`'s
/// converter path by checking the resulting sample count against the fixture's own duration.
final class AudioFileTests: XCTestCase {
    private func assertSampleCount(forFixtureNamed name: String) throws {
        let bundle = Bundle(for: Self.self)
        let url = try XCTUnwrap(bundle.url(forResource: name, withExtension: "wav", subdirectory: "Fixtures"))
        let file = try AVAudioFile(forReading: url)
        let expected = Int(Double(file.length) / file.fileFormat.sampleRate * 16_000)

        let samples = try AudioFile.load16k(url)

        let tolerance = max(1, Int(Double(expected) * 0.01))
        let diff = abs(samples.count - expected)
        XCTAssertLessThanOrEqual(
            diff, tolerance,
            "\(name).wav: expected ~\(expected) samples (±\(tolerance), 1 %) at 16 kHz, got \(samples.count) (diff \(diff))")
    }

    func testEnSampleCountMatchesDuration() throws {
        try assertSampleCount(forFixtureNamed: "en")
    }

    func testSilenceSampleCountMatchesDuration() throws {
        try assertSampleCount(forFixtureNamed: "silence")
    }
}

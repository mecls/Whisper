import XCTest
@testable import Voice

/// The audio a streamed dictation leaves untranscribed, and whether it is worth a pass.
///
/// `AudioStreamTranscriber` only transcribes once a full second of new audio has arrived, and runs
/// no final pass when it stops — so whatever arrived after the last pass started is never
/// transcribed. That was assumed to be a sub-second rounding error. The first dictation that
/// actually measured it left **4396 ms of 15295 ms** untranscribed: 29% of what the user said,
/// missing from the paste while the bar had shown the whole sentence.
///
/// `Coordinator.tail` decides what to hand to a final pass. Both of its guards exist to stop the
/// fix being worse than the bug: Whisper hallucinates words on very short clips and on silence, and
/// a hallucinated sentence appended to a correct one is worse than a missing tail.
@MainActor
final class StreamTailTests: XCTestCase {

    /// 16 kHz, loud enough to clear `EnergyGate`'s 0.01 threshold comfortably.
    private func speech(ms: Int) -> [Float] {
        (0..<(16 * ms)).map { 0.2 * sin(Float($0) * 0.1) }
    }

    private func silence(ms: Int) -> [Float] {
        (0..<(16 * ms)).map { _ in Float.random(in: -0.0005...0.0005) }
    }

    func testTheUntranscribedTailIsReturnedForAFinalPass() throws {
        // The real shape: 15295 ms recorded, the stream reached 10899 ms.
        let samples = speech(ms: 15295)
        let tail = try XCTUnwrap(Coordinator.tail(of: samples, afterMs: 10899, totalMs: 15295),
                                 "4396 ms of speech went untranscribed; it must be passed on, not dropped.")
        // 4396/15295 of the recording, within a sample or two of rounding.
        XCTAssertEqual(Double(tail.count), Double(samples.count) * 4396 / 15295, accuracy: 4)
    }

    func testAGapTooShortToBeAWordIsNotWorthAPass() {
        let samples = speech(ms: 5000)
        XCTAssertNil(Coordinator.tail(of: samples, afterMs: 4800, totalMs: 5000),
                     "200 ms is a rounding error, and a clip that short is what Whisper hallucinates on.")
        XCTAssertNotNil(Coordinator.tail(of: samples, afterMs: 4500, totalMs: 5000),
                        "500 ms is over the threshold and can hold a word.")
    }

    /// The ordinary case of stopping talking a beat before letting go of the key. Transcribing that
    /// gap invents words; `EnergyGate` is the same gate that decides whether a whole dictation
    /// contains speech, so it decides this too.
    func testASilentGapIsNotTranscribed() {
        let samples = speech(ms: 5000) + silence(ms: 2000)
        XCTAssertNil(Coordinator.tail(of: samples, afterMs: 5000, totalMs: 7000))
    }

    func testNothingLeftOverProducesNoPass() {
        let samples = speech(ms: 5000)
        XCTAssertNil(Coordinator.tail(of: samples, afterMs: 5000, totalMs: 5000))
    }

    /// The stream can report a segment end slightly past the recorded length; that must not index
    /// past the buffer.
    func testAnOverrunningCoveredLengthIsSafe() {
        let samples = speech(ms: 5000)
        XCTAssertNil(Coordinator.tail(of: samples, afterMs: 6000, totalMs: 5000))
        XCTAssertNil(Coordinator.tail(of: [], afterMs: 0, totalMs: 5000))
        XCTAssertNil(Coordinator.tail(of: samples, afterMs: 0, totalMs: 0))
    }
}

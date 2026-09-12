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
        // 4396 ms of new audio plus the overlap, which starts the pass before the boundary.
        let expectedMs = Double(15295 - (10899 - Coordinator.overlapMs))
        XCTAssertEqual(Double(tail.count), Double(samples.count) * expectedMs / 15295, accuracy: 4)
    }

    /// The pass must start *before* the boundary, or the word straddling it is sliced in half in
    /// the audio, transcribed wrongly, and then preferred over the stream's copy by `Stitch` —
    /// which is how "and" went missing from a dictation.
    func testThePassStartsBeforeTheBoundarySoNoWordIsSplit() throws {
        let samples = speech(ms: 10000)
        let tail = try XCTUnwrap(Coordinator.tail(of: samples, afterMs: 8000, totalMs: 10000))
        let butted = Double(samples.count) * 2000 / 10000
        XCTAssertGreaterThan(Double(tail.count), butted,
                             "The tail starts exactly at the boundary, so the word crossing it is cut in half.")
        XCTAssertEqual(Double(tail.count), Double(samples.count) * 3500 / 10000, accuracy: 4)
    }

    /// The overlap must not turn a gap too small to matter into one worth transcribing: the
    /// decision is about the real gap, the padding only about where the pass starts.
    func testTheOverlapDoesNotResurrectATrivialGap() {
        let samples = speech(ms: 5000)
        XCTAssertNil(Coordinator.tail(of: samples, afterMs: 4800, totalMs: 5000))
    }

    /// A boundary earlier than the overlap must clamp to the start rather than index negatively.
    func testABoundaryInsideTheOverlapClampsToTheStart() throws {
        let samples = speech(ms: 4000)
        let tail = try XCTUnwrap(Coordinator.tail(of: samples, afterMs: 500, totalMs: 4000))
        XCTAssertEqual(tail.count, samples.count, "Clamped to zero, so the whole recording is passed.")
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
        XCTAssertNil(Coordinator.tail(of: samples, afterMs: 5000, totalMs: 7000),
                     "The gap is silence. The overlap reaches back into the speech before it, so this only holds if the decision is made on the new audio alone.")
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

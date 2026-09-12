import WhisperKit
import XCTest
@testable import Voice

/// The streamed transcript must contain everything the user said, including the tail.
///
/// `AudioStreamTranscriber` confirms `segments.count - requiredSegmentsForConfirmation` segments and
/// that constant defaults to 2, so the **last two segments are never confirmed**. They are not
/// half-revised guesses; they are simply the end of the dictation, with no further audio coming to
/// settle them. `finish()` originally returned confirmed segments only, which truncated the end of
/// every streamed dictation — and did it invisibly, because the bar renders confirmed *plus*
/// unconfirmed, so the full sentence appeared on screen and a shortened one arrived in the document.
@MainActor
final class StreamingTranscriberTests: XCTestCase {

    private func segment(_ text: String, start: Float, end: Float) -> TranscriptionSegment {
        TranscriptionSegment(start: start, end: end, text: text)
    }

    func testTheUnconfirmedTailIsPasted() async {
        let t = StreamingTranscriber()
        t.absorb(
            confirmed: [segment("General shows.", start: 0, end: 2),
                        segment(" I tried the streaming fix.", start: 2, end: 5)],
            unconfirmed: [segment(" It's working quite okay.", start: 5, end: 7),
                          segment(" The only problem is it cuts the speech.", start: 7, end: 10)])

        let result = await t.finish()
        let text = try? XCTUnwrap(result?.text)
        XCTAssertNotNil(text)
        XCTAssertTrue(text!.contains("The only problem is it cuts the speech."),
                      "The tail was dropped — this is the truncation bug. Got: \(text!)")
        XCTAssertTrue(text!.hasPrefix("General shows."))
        XCTAssertEqual(result?.segments, 4, "SkipGate counts chunk boundaries, so unconfirmed segments count too.")
    }

    /// The case that hid the bug. A dictation short enough to be two segments confirms *nothing*, so
    /// `finish()` returned nil and the dictation quietly took a full one-pass transcription — correct
    /// output, none of the speed, and no sign anything was wrong.
    func testAShortDictationIsStreamedRatherThanFallingBack() async {
        let t = StreamingTranscriber()
        t.absorb(confirmed: [], unconfirmed: [segment("Turn the lights off.", start: 0, end: 2)])

        let result = await t.finish()
        XCTAssertEqual(result?.text, "Turn the lights off.",
                       "Nothing was confirmed, so this used to fall back to a full transcription.")
    }

    /// Falling back is still right when there is genuinely nothing — a mis-tap, or silence.
    func testSilenceStillFallsBack() async {
        let t = StreamingTranscriber()
        t.absorb(confirmed: [], unconfirmed: [])
        let result = await t.finish()
        XCTAssertNil(result)
    }

    /// Confirmed and unconfirmed are disjoint halves of one transcript, so joining them must not
    /// double a space or lose the boundary between them.
    func testTheJoinDoesNotDoubleSpaceOrRunWordsTogether() async {
        let t = StreamingTranscriber()
        t.absorb(confirmed: [segment("One two.", start: 0, end: 1)],
                 unconfirmed: [segment("  Three four.", start: 1, end: 2)])
        let result = await t.finish()
        XCTAssertEqual(result?.text, "One two. Three four.")
    }

    /// `coveredMs` is what tells us whether the stream reached the end of the recording; if it stops
    /// tracking the tail, the gap measurement silently reads zero and hides a real loss.
    func testCoveredMsTracksTheFurthestSegment() async {
        let t = StreamingTranscriber()
        t.absorb(confirmed: [segment("a", start: 0, end: 3)], unconfirmed: [segment("b", start: 3, end: 8.5)])
        XCTAssertEqual(t.coveredMs, 8500)
    }
}

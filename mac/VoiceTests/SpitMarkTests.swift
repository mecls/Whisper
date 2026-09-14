import XCTest
@testable import Voice

/// The spit mark in the mic button while listening: a face that stays put and sound-wave arcs that
/// follow the voice.
///
/// The arcs are found, not hard-coded: a new version of the mark only has to keep its parts a few
/// empty columns apart. The face is itself full of single empty columns — it is dithered — so the
/// split has to tell a dither gap from the space between two arcs, or the face falls apart into a
/// dozen "arcs" and the button lights the jaw in response to the voice.
final class SpitMarkTests: XCTestCase {

    func testTheMarkIsASquareGrid() {
        let rows = SpitMarkGrid.rows
        XCTAssertFalse(rows.isEmpty)
        XCTAssertTrue(rows.allSatisfy { $0.count == rows.count },
                      "Every row must be as long as there are rows, or the dots are drawn skewed.")
    }

    func testTheShippedMarkSplitsIntoAFaceAndFourArcs() {
        let parts = SpitMark.parts(SpitMarkGrid.rows)
        XCTAssertEqual(parts.arcs.count, 4)
        XCTAssertEqual(parts.face.count, 712)
        XCTAssertEqual(parts.arcs.map(\.count), [27, 29, 28, 30], "Arcs must come out inside first.")
    }

    /// One empty column is dither, not a boundary.
    func testANarrowGapDoesNotSplitAPart() {
        let parts = SpitMark.parts([
            "#.#...#",
            "#.#...#",
            ".......",
            ".......",
            ".......",
            ".......",
            ".......",
        ])
        XCTAssertEqual(parts.face.count, 4)
        XCTAssertEqual(parts.arcs.count, 1)
    }

    func testSilenceLightsNoArcs() {
        XCTAssertEqual(SpitMark.litArcs(level: 0, of: 4), 0)
        // The room's noise floor, not speech. Lighting an arc here would make the mark flicker
        // while nobody is talking.
        XCTAssertEqual(SpitMark.litArcs(level: 0.001, of: 4), 0)
    }

    /// The same scale as the waveform beside it, which is full height at 0.025 — so the two agree
    /// on what "loud" is.
    func testFullVolumeLightsEveryArc() {
        XCTAssertEqual(SpitMark.litArcs(level: 0.025, of: 4), 4)
        XCTAssertEqual(SpitMark.litArcs(level: 0.5, of: 4), 4)
    }

    func testArcsLightFromTheInsideOutAsTheVoiceRises() {
        let lit = stride(from: Float(0), through: 0.03, by: 0.001).map { SpitMark.litArcs(level: $0, of: 4) }
        XCTAssertEqual(lit, lit.sorted(), "A louder voice must never light fewer arcs.")
        XCTAssertEqual(SpitMark.litArcs(level: 0.0125, of: 4), 2)
    }

    func testANonsenseLevelLightsNothing() {
        XCTAssertEqual(SpitMark.litArcs(level: -1, of: 4), 0)
        XCTAssertEqual(SpitMark.litArcs(level: .nan, of: 4), 0)
    }
}

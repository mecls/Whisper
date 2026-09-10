import XCTest
@testable import Voice

final class EnergyGateTests: XCTestCase {
    func testSilenceIsNotSpeech() {
        XCTAssertFalse(EnergyGate.hasSpeech([Float](repeating: 0, count: 16000)))
    }
    func testToneIsSpeech() {
        let tone = (0..<16000).map { Float(sin(Double($0) * 2 * .pi * 220 / 16000)) * 0.2 }
        XCTAssertTrue(EnergyGate.hasSpeech(tone))
    }
    func testOneLoudClickInSilenceIsNotSpeech() {
        var xs = [Float](repeating: 0, count: 16000)
        xs[8000] = 0.9
        XCTAssertFalse(EnergyGate.hasSpeech(xs)) // 95th percentile of 20 ms frames stays ~0
    }
}

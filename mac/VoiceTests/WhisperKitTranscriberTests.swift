import XCTest
@testable import Voice

/// Opt-in: exercises the real WhisperKit pipeline against the pre-seeded model folder (no download).
/// Skips unless VOICE_ASR_TESTS=1, so the unattended `xcodebuild test` run stays fast.
final class WhisperKitTranscriberTests: XCTestCase {
    func testTranscribesFixturesWithinBudget() async throws {
        try XCTSkipUnless(
            ProcessInfo.processInfo.environment["VOICE_ASR_TESTS"] == "1",
            "set VOICE_ASR_TESTS=1 (model must already be seeded under Application Support)")

        let bundle = Bundle(for: Self.self)

        // silence.wav: the app never hands silence to the transcriber (EnergyGate gates it first
        // in Coordinator); the test only confirms the gate rejects this fixture.
        let silenceURL = bundle.url(forResource: "silence", withExtension: "wav", subdirectory: "Fixtures")!
        let silenceSamples = try AudioFile.load16k(silenceURL)
        XCTAssertFalse(EnergyGate.hasSpeech(silenceSamples), "silence fixture unexpectedly read as speech")

        let t = WhisperKitTranscriber(modelId: "large-v3-v20240930_turbo_632MB")

        let loadStart = Date()
        try await t.prepare { _ in }
        let loadSecs = Date().timeIntervalSince(loadStart)
        print("[ASR timing] model load (first call, includes CoreML compile if not cached): \(loadSecs)s")

        let enURL = bundle.url(forResource: "en", withExtension: "wav", subdirectory: "Fixtures")!
        let enSamples = try AudioFile.load16k(enURL)
        let hint = TranscribeHint(language: nil, vocabulary: ["Miraside", "Convex"])

        let firstStart = Date()
        let first = try await t.transcribe(enSamples, hint: hint, progress: nil)
        let firstSecs = Date().timeIntervalSince(firstStart)
        print("[ASR timing] first transcription (en.wav): \(firstSecs)s")
        XCTAssertLessThan(firstSecs, 3.0, "en.wav first transcription took \(firstSecs)s")
        XCTAssertFalse(first.text.isEmpty)
        XCTAssertEqual(first.language, "en")

        let secondStart = Date()
        let second = try await t.transcribe(enSamples, hint: hint, progress: nil)
        let secondSecs = Date().timeIntervalSince(secondStart)
        print("[ASR timing] second transcription (en.wav): \(secondSecs)s")
        XCTAssertLessThan(secondSecs, 3.0, "en.wav second transcription took \(secondSecs)s")
        XCTAssertFalse(second.text.isEmpty)
        XCTAssertEqual(second.language, "en")
    }
}

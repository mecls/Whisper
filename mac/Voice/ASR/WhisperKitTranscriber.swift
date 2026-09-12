import Foundation
import WhisperKit

/// On-device Whisper. Options per docs/PLAN.md §3.4: no temperature fallbacks, explicit language detection,
/// no timestamps, dictionary terms as prompt tokens, VAD chunking for clips over 30 s.
final class WhisperKitTranscriber: Transcriber {
    private let modelId: String
    private let manager = ModelManager()
    private var pipe: WhisperKit?

    init(modelId: String) { self.modelId = modelId }

    var isReady: Bool { pipe != nil }

    /// The loaded pipeline, for `StreamingTranscriber` to build an `AudioStreamTranscriber` from.
    /// One model instance, not two: the compressed model is ~630 MB and takes minutes to compile.
    var whisperKit: WhisperKit? { pipe }

    func prepare(progress: @escaping (Double) -> Void) async throws {
        let folder = try await manager.ensure(modelId) { progress($0 * 0.8) }
        let config = WhisperKitConfig(
            modelFolder: folder.path,
            computeOptions: ModelComputeOptions(audioEncoderCompute: .cpuAndNeuralEngine, textDecoderCompute: .cpuAndGPU),
            logLevel: .error, prewarm: true, load: true, download: false)
        progress(0.85)
        let p = try await WhisperKit(config)
        progress(1)
        pipe = p
        // The first real transcription after load is ~2x slower even after prewarm (docs/SPIKES.md): warm it with 1 s of silence.
        _ = try? await p.transcribe(audioArray: [Float](repeating: 0, count: 16000), decodeOptions: DecodingOptions(temperatureFallbackCount: 0, withoutTimestamps: true))
    }

    func transcribe(_ samples: [Float], hint: TranscribeHint, progress: ((Double) -> Void)?) async throws -> Transcript {
        guard let pipe else { throw TranscriberError.notReady }
        let t0 = Date()
        let promptTokens = hint.vocabulary.isEmpty ? nil : pipe.tokenizer?.encode(text: " " + hint.vocabulary.joined(separator: ", ")).prefix(150).map { $0 }
        let long = samples.count > 30 * 16000
        let total = Double(samples.count) / 16000
        // TranscriptionProgress has no end-time field to derive elapsed-fraction from (amendment B3).
        // When chunking, report windowId / expected-window-count; otherwise leave the HUD indeterminate.
        let expectedWindows = max(1, Int((total / 30).rounded(.up)))

        // Named `callback:` (not a trailing closure) and an explicit `[TranscriptionResult]` return
        // type: with a trailing closure, overload resolution picks the deprecated
        // `transcribe(audioArray:decodeOptions:callback:) -> TranscriptionResult?` overload instead
        // of the current `…-> [TranscriptionResult]` one (amendment B3: adapt and report — the
        // brief's trailing-closure form does not compile against 0.18.0's overload set).
        func decode(promptTokens: [Int]?, reportProgress: Bool) async throws -> [TranscriptionResult] {
            let opts = DecodingOptions(
                task: .transcribe,
                language: hint.language,
                temperature: 0,
                temperatureFallbackCount: 0,
                usePrefillPrompt: true,
                detectLanguage: hint.language == nil,
                skipSpecialTokens: true,
                withoutTimestamps: true,
                wordTimestamps: false,
                promptTokens: promptTokens,
                compressionRatioThreshold: 2.4,
                logProbThreshold: -1.0,
                noSpeechThreshold: 0.6,
                concurrentWorkerCount: long ? 2 : 1,
                chunkingStrategy: long ? ChunkingStrategy.vad : ChunkingStrategy.none)
            let progressCallback: (TranscriptionProgress) -> Bool? = { p in
                if long, reportProgress { progress?(min(1, Double(p.windowId + 1) / Double(expectedWindows))) }
                return nil
            }
            return try await pipe.transcribe(audioArray: samples, decodeOptions: opts, callback: progressCallback)
        }

        var results = try await decode(promptTokens: promptTokens, reportProgress: true)
        var text = results.map(\.text).joined(separator: " ").trimmingCharacters(in: .whitespacesAndNewlines)
        if text.isEmpty, promptTokens != nil {
            // Adaptation (see task-7-report.md): with temperatureFallbackCount: 0 (one decode attempt,
            // no retry at a higher temperature — brief's "no temperature fallbacks"), a non-nil
            // promptTokens reproducibly made argmax-oss-swift 0.18.0's single attempt come back flagged
            // needsFallback (no-speech/low-logprob) with nothing left to fall back to, returning empty
            // text on real speech (verified against the en fixture, independent of promptTokens content,
            // language, detectLanguage, or usePrefillCache). Retry once without the vocabulary prompt
            // rather than surface "Nothing heard" on real speech; the common case still gets the
            // dictionary-biased first attempt. reportProgress: false here — the first attempt already
            // reported up to wherever it got to, and a second full pass restarting from window 0 would
            // otherwise show the HUD bar jump backwards on clips over 30 s (fix round 1, F3).
            results = try await decode(promptTokens: nil, reportProgress: false)
            text = results.map(\.text).joined(separator: " ").trimmingCharacters(in: .whitespacesAndNewlines)
        }
        let lang = results.first?.language ?? hint.language ?? "unknown"
        return Transcript(text: text, language: lang, durationMs: Int(Date().timeIntervalSince(t0) * 1000))
    }
}

enum TranscriberError: Error { case notReady }

import Foundation
import WhisperKit
import os

private let log = Logger(subsystem: "co.miraside.voice", category: "streaming")

/**
 Live transcription: WhisperKit's `AudioStreamTranscriber` driven off our own microphone.

 Two things it produces, and they matter for different reasons.

 **Live text for the bar**, so a 30-second hands-free session is not 30 seconds of staring at a
 waveform wondering whether the words are right. It goes to Voice's own bar and never into the
 target application: Whisper revises unconfirmed segments as it goes, and text already pasted into
 someone's document cannot be un-typed.

 **The final transcript**, which is the larger win. Because the confirmed segments are already
 transcribed when the key comes up, release→paste no longer contains a full ASR pass.

 Segment *count* is exposed alongside the text because the skip gate needs it: a transcript
 assembled from one segment has no chunk boundary and is safe to paste uncleaned, where one built
 from several is not.
 */
@MainActor
final class StreamingTranscriber: ObservableObject {
    /// Text Whisper has settled on. This is what gets pasted.
    @Published private(set) var confirmedText = ""
    /// Whisper's current hypothesis for the tail. Shown dimmed; never pasted.
    @Published private(set) var unconfirmedText = ""
    /// How many confirmed segments the transcript was assembled from (see `SkipGate`).
    @Published private(set) var confirmedSegmentCount = 0

    private var streamer: AudioStreamTranscriber?
    private var task: Task<Void, Never>?

    var isRunning: Bool { task != nil }

    /// Begins streaming off the already-running recorder. Failing here is not fatal: the dictation
    /// continues and falls back to a one-pass transcription when it ends.
    func start(pipe: WhisperKit, processor: VoiceAudioProcessor, hint: TranscribeHint) {
        guard task == nil, let tokenizer = pipe.tokenizer else { return }
        reset()

        let prompt = hint.vocabulary.isEmpty
            ? nil
            : tokenizer.encode(text: " " + hint.vocabulary.joined(separator: ", ")).prefix(150).map { $0 }

        // The same language and vocabulary the one-pass path uses. A streamed dictation that spells
        // a dictionary term wrong where a held one gets it right would read as the feature making
        // things worse.
        let options = DecodingOptions(
            task: .transcribe,
            language: hint.language,
            temperature: 0,
            temperatureFallbackCount: 0,
            usePrefillPrompt: true,
            detectLanguage: hint.language == nil,
            skipSpecialTokens: true,
            withoutTimestamps: false,   // streaming needs segment times to decide what is settled
            wordTimestamps: false,
            promptTokens: prompt,
            compressionRatioThreshold: 2.4,
            logProbThreshold: -1.0,
            noSpeechThreshold: 0.6)

        let streamer = AudioStreamTranscriber(
            audioEncoder: pipe.audioEncoder,
            featureExtractor: pipe.featureExtractor,
            segmentSeeker: pipe.segmentSeeker,
            textDecoder: pipe.textDecoder,
            tokenizer: tokenizer,
            audioProcessor: processor,
            decodingOptions: options
        ) { [weak self] _, newState in
            // Posted from the actor; every field below belongs to main.
            Task { @MainActor [weak self] in self?.absorb(newState) }
        }
        self.streamer = streamer

        task = Task { [weak self] in
            do {
                try await streamer.startStreamTranscription()
            } catch {
                // Never fatal. The buffered audio is still there and the dictation falls back to a
                // single pass at the end — losing a dictation to a streaming failure is the one
                // outcome this must not produce.
                log.error("live transcription unavailable: \(error.localizedDescription, privacy: .public)")
            }
            await MainActor.run { self?.task = nil }
        }
    }

    private func absorb(_ state: AudioStreamTranscriber.State) {
        confirmedSegmentCount = state.confirmedSegments.count
        confirmedText = state.confirmedSegments.map(\.text).joined().trimmingCharacters(in: .whitespaces)
        unconfirmedText = state.unconfirmedSegments.map(\.text).joined().trimmingCharacters(in: .whitespaces)
        // Lengths only, never content: this fires many times per dictation and the app's logs must
        // never carry what the user said.
        log.debug("stream: \(self.confirmedSegmentCount, privacy: .public) segments, \(self.confirmedText.count, privacy: .public) chars")
    }

    /// Ends the stream and returns the text to paste, or nil when there is nothing usable and the
    /// caller should fall back to a one-pass transcription.
    ///
    /// Only confirmed segments: unconfirmed ones are hypotheses Whisper has not settled on, and
    /// including them is how a half-revised word reaches someone's document.
    func finish() async -> (text: String, segments: Int)? {
        task?.cancel()
        task = nil
        if let streamer { await streamer.stopStreamTranscription() }
        streamer = nil

        let text = confirmedText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        return (text, confirmedSegmentCount)
    }

    func reset() {
        confirmedText = ""
        unconfirmedText = ""
        confirmedSegmentCount = 0
    }
}

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
    /// Text Whisper has settled on and will not revise.
    @Published private(set) var confirmedText = ""
    /// Whisper's current hypothesis for the tail — the last two segments, which `AudioStreamTranscriber`
    /// never confirms. Shown dimmed while the stream runs, because it can still change; appended to
    /// the transcript at the end, because by then it cannot (see `finish`).
    @Published private(set) var unconfirmedText = ""
    /// How many segments the transcript was assembled from (see `SkipGate`).
    @Published private(set) var confirmedSegmentCount = 0
    /// How far into the dictation Whisper's segments actually reach, in seconds. Compared against
    /// the recorded length at the end to measure what the stream never got to (see `finish`).
    private var coveredSeconds: Double = 0
    /// Segment spans, for the diagnostic in `finish`. Times only, never text.
    private var spans: [(start: Float, end: Float)] = []

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

        log.info("live transcription starting")
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
        absorb(confirmed: state.confirmedSegments, unconfirmed: state.unconfirmedSegments)
    }

    /// Split out from the `State` overload above so it can be tested: `AudioStreamTranscriber.State`
    /// is a public type whose memberwise initialiser is not, so a test cannot build one.
    func absorb(confirmed: [TranscriptionSegment], unconfirmed: [TranscriptionSegment]) {
        confirmedSegmentCount = confirmed.count + unconfirmed.count
        confirmedText = confirmed.map(\.text).joined().trimmingCharacters(in: .whitespaces)
        unconfirmedText = unconfirmed.map(\.text).joined().trimmingCharacters(in: .whitespaces)
        let lastEnd = unconfirmed.last?.end ?? confirmed.last?.end
        if let lastEnd { coveredSeconds = max(coveredSeconds, Double(lastEnd)) }
        spans = (confirmed + unconfirmed).map { (start: $0.start, end: $0.end) }
        // Lengths only, never content: this fires many times per dictation and the app's logs must
        // never carry what the user said.
        log.debug("stream: \(self.confirmedSegmentCount, privacy: .public) segments, \(self.confirmedText.count, privacy: .public) chars")
    }

    /// Ends the stream and returns the text to paste, or nil when there is nothing usable and the
    /// caller should fall back to a one-pass transcription.
    ///
    /// **Confirmed *and* unconfirmed**, which is the opposite of what this did at first. The
    /// original reasoning — "unconfirmed segments are hypotheses Whisper has not settled on" — is
    /// right while the stream is running and wrong the moment it stops, and getting that backwards
    /// silently truncated the end of every single streamed dictation.
    ///
    /// The mechanism is in `AudioStreamTranscriber`: it confirms
    /// `segments.count - requiredSegmentsForConfirmation` and `requiredSegmentsForConfirmation`
    /// defaults to 2, so the last two segments are *permanently* unconfirmed. They are not
    /// mid-revision, they are simply the tail, and no further audio is coming to settle them. A
    /// dictation short enough to be two segments confirmed nothing at all and fell back to a full
    /// one-pass transcription — which is why this looked like it worked.
    ///
    /// The bar was already showing `confirmedText + unconfirmedText`, so the user watched the whole
    /// sentence appear and then got a cut-off paste.
    func finish() async -> (text: String, segments: Int)? {
        task?.cancel()
        task = nil
        if let streamer { await streamer.stopStreamTranscription() }
        streamer = nil

        let text = ([confirmedText, unconfirmedText]
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
            .joined(separator: " "))
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else {
            // Loud on purpose. This is the silent-fallback path, and when the VAD scale was wrong
            // it took every dictation for hours without a single line in the log to say so.
            log.error("live transcription produced nothing — falling back to a one-pass transcription")
            return nil
        }
        // Spans, not text. A dictation that comes back short but ends cleanly lost words in the
        // middle or at the start, and the only thing that localises it is where the segments
        // actually sit: a hole between one segment's end and the next one's start is audio Whisper
        // produced no words for.
        let map = self.spans.map { "\(Int($0.start * 1000))-\(Int($0.end * 1000))" }.joined(separator: " ")
        let holes = zip(self.spans, self.spans.dropFirst())
            .compactMap { a, b in b.start - a.end > 0.25 ? "\(Int(a.end * 1000))-\(Int(b.start * 1000))" : nil }
        log.info("live transcription used: \(self.confirmedSegmentCount, privacy: .public) segments, covering \(Int(self.coveredSeconds * 1000), privacy: .public) ms of audio")
        log.info("segment spans: \(map, privacy: .public)\(holes.isEmpty ? "" : "  HOLES: " + holes.joined(separator: " "), privacy: .public)")
        return (text, confirmedSegmentCount)
    }

    /// How far the segments reach, for the caller to compare against the recorded length.
    var coveredMs: Int { Int(coveredSeconds * 1000) }

    func reset() {
        confirmedText = ""
        unconfirmedText = ""
        confirmedSegmentCount = 0
        coveredSeconds = 0
        spans = []
    }
}

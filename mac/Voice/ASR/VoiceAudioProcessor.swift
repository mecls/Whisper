import AVFoundation
import CoreML
import Foundation
import WhisperKit

/**
 Lets WhisperKit's `AudioStreamTranscriber` read our microphone without owning it.

 `AudioStreamTranscriber.startStreamTranscription()` calls `audioProcessor.startRecordingLive`, so
 left to itself it would open the input device directly. That is not acceptable here: `AudioRecorder`
 is where the `AVAudioEngineConfigurationChange` rebuild lives (AirPods move the input from 48 kHz to
 24 kHz and the converter has to follow), where the 90-second cap lives, and where the bar's waveform
 gets its levels. Handing capture to WhisperKit would re-open a bug closed on 2026-09-12 and verified
 across seven live device switches.

 `AudioProcessing` declares a dozen members but the streaming actor only ever calls four —
 `startRecordingLive`, `stopRecording`, `relativeEnergy` and `audioSamples`. Everything else either
 delegates to WhisperKit's own `AudioProcessor` (the file-loading statics, which have nothing to do
 with live capture) or is unreachable. `AudioRecorder` itself is not modified by any of this.
 */
final class VoiceAudioProcessor: AudioProcessing {
    private let recorder: AudioRecorder

    init(recorder: AudioRecorder) { self.recorder = recorder }

    // MARK: - The four members streaming actually uses

    /// Everything captured this session, without consuming it. The buffer is append-only until the
    /// dictation ends, so the indices `AudioStreamTranscriber` tracks through it stay valid.
    var audioSamples: ContiguousArray<Float> { ContiguousArray(recorder.capturedSamples) }

    /// The same RMS values the bar's waveform draws. Deliberately one measurement, not two: an
    /// independent energy calculation would drift from the waveform, and the VAD deciding whether
    /// the user is speaking would then disagree with what the user can see.
    var relativeEnergy: [Float] { recorder.recentEnergy }

    var relativeEnergyWindow: Int = 20

    func startRecordingLive(inputDeviceID: DeviceID?, callback: (([Float]) -> Void)?) throws {
        // `inputDeviceID` is ignored on purpose: device selection belongs to the OS, and
        // AudioRecorder already rebuilds its capture chain when the device changes underneath it.
        // The recorder is started by the Coordinator as part of the dictation, so there is nothing
        // to start here — this exists so the streaming actor's own start path is a no-op.
        if !recorder.isRunning { try recorder.start() }
    }

    func stopRecording() {
        // Not `recorder.stop()`: that drains the buffer and returns the samples, which is the
        // Coordinator's job at the end of a dictation. If this drained it, the final transcript
        // would be assembled from an empty buffer.
    }

    // MARK: - Declared but never reached during streaming

    func pauseRecording() {}

    func resumeRecordingLive(inputDeviceID: DeviceID?, callback: (([Float]) -> Void)?) throws {
        try startRecordingLive(inputDeviceID: inputDeviceID, callback: callback)
    }

    func startStreamingRecordingLive(inputDeviceID: DeviceID?) -> (AsyncThrowingStream<[Float], Error>, AsyncThrowingStream<[Float], Error>.Continuation) {
        // Unreachable: `AudioStreamTranscriber` uses the callback form. Returning an empty stream
        // rather than trapping, so an upstream change to WhisperKit degrades to "no live text"
        // instead of crashing the app mid-dictation.
        var continuation: AsyncThrowingStream<[Float], Error>.Continuation!
        let stream = AsyncThrowingStream<[Float], Error> { continuation = $0 }
        continuation.finish()
        return (stream, continuation)
    }

    /// Never called by streaming; our buffer is fixed-capacity and has no unbounded growth to trim.
    func purgeAudioSamples(keepingLast keep: Int) {}

    // MARK: - File loading — nothing to do with live capture

    func padOrTrim(fromArray audioArray: [Float], startAt startIndex: Int, toLength frameLength: Int) -> (any AudioProcessorOutputType)? {
        // `saveSegment` is left at its default of `false` and must stay there: it writes the audio
        // segment to disk for debugging, which would break the app's one stated privacy property —
        // that transcripts and audio never touch this Mac's disk — with nothing in this codebase
        // to show for it.
        AudioProcessor.padOrTrimAudio(fromArray: audioArray, startAt: startIndex, toLength: frameLength)
    }

    static func loadAudio(fromPath audioFilePath: String, channelMode: ChannelMode, startTime: Double?, endTime: Double?, maxReadFrameSize: AVAudioFrameCount?) throws -> AVAudioPCMBuffer {
        try AudioProcessor.loadAudio(fromPath: audioFilePath, channelMode: channelMode, startTime: startTime, endTime: endTime, maxReadFrameSize: maxReadFrameSize)
    }

    static func loadAudio(at audioPaths: [String], channelMode: ChannelMode) async -> [Result<[Float], Swift.Error>] {
        await AudioProcessor.loadAudio(at: audioPaths, channelMode: channelMode)
    }

    static func padOrTrimAudio(fromArray audioArray: [Float], startAt startIndex: Int, toLength frameLength: Int, saveSegment: Bool) -> MLMultiArray? {
        // Forwarded with `saveSegment` as given, but nothing in this app ever passes `true`.
        AudioProcessor.padOrTrimAudio(fromArray: audioArray, startAt: startIndex, toLength: frameLength, saveSegment: saveSegment)
    }
}

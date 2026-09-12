import AVFoundation
import XCTest
@testable import Voice

/// The AirPods bug, pinned down.
///
/// `AudioRecorder` builds an `AVAudioConverter` and installs a tap for one specific input format.
/// Connecting AirPods makes them the default input and changes that format underneath both. The
/// failure is silent and nasty: the engine keeps delivering buffers, the converter keeps reporting
/// success, and what comes out is near-silence — loud enough to clear the energy gate, empty enough
/// that the transcriber returns nothing. A dictation reaches the server carrying zero words and the
/// app looks like it has simply stopped hearing you.
///
/// `formatsMatch` is the guard that turns that into a detected, recoverable condition. The engine
/// itself needs a microphone and cannot be exercised here, so this covers the decision rather than
/// the plumbing.
final class AudioFormatTests: XCTestCase {

    private func fmt(_ rate: Double, _ channels: AVAudioChannelCount) -> AVAudioFormat {
        AVAudioFormat(standardFormatWithSampleRate: rate, channels: channels)!
    }

    func testIdenticalFormatsMatch() {
        XCTAssertTrue(AudioRecorder.formatsMatch(fmt(48_000, 1), fmt(48_000, 1)))
        XCTAssertTrue(AudioRecorder.formatsMatch(fmt(16_000, 2), fmt(16_000, 2)))
    }

    func testASampleRateChangeIsCaught() {
        // The MacBook mic runs at 48 kHz; AirPods in headset mode drop to 24 kHz or lower. This is
        // the exact transition that produced an empty dictation.
        XCTAssertFalse(AudioRecorder.formatsMatch(fmt(24_000, 1), fmt(48_000, 1)))
        XCTAssertFalse(AudioRecorder.formatsMatch(fmt(48_000, 1), fmt(24_000, 1)))
    }

    func testAChannelCountChangeIsCaught() {
        // A stereo interface replaced by a mono headset converts "successfully" into noise.
        XCTAssertFalse(AudioRecorder.formatsMatch(fmt(48_000, 2), fmt(48_000, 1)))
    }

    func testAMissingFormatNeverMatches() {
        // nil is "we have no capture chain yet", which must always trigger a rebuild rather than
        // be treated as compatible with whatever arrives.
        XCTAssertFalse(AudioRecorder.formatsMatch(nil, fmt(48_000, 1)))
        XCTAssertFalse(AudioRecorder.formatsMatch(fmt(48_000, 1), nil))
        XCTAssertFalse(AudioRecorder.formatsMatch(nil, nil))
    }

    func testMatchIgnoresPropertiesTheConverterDoesNotCareAbout() {
        // Interleaving differs between devices and is handled by the converter itself; treating it
        // as a mismatch would rebuild the capture chain on every buffer for no reason.
        let planar = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48_000, channels: 1, interleaved: false)!
        let packed = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48_000, channels: 1, interleaved: true)!
        XCTAssertTrue(AudioRecorder.formatsMatch(planar, packed))
    }

    func testNoInputDeviceIsADistinctError() {
        // `start()` throws this rather than recording nothing, so the Coordinator can tell the user
        // the microphone is gone instead of showing "Listening" over a dead engine.
        XCTAssertEqual(AudioRecorderError.noInputDevice, AudioRecorderError.noInputDevice)
    }
}

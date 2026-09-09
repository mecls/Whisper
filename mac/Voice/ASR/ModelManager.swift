import Foundation
import WhisperKit

/// Locates and downloads WhisperKit CoreML model folders under the app's own Application Support
/// directory (not WhisperKit's default `~/Documents/huggingface`). Layout verified against
/// argmax-oss-swift 0.18.0 in the day-0 spike (docs/SPIKES.md):
/// `<downloadBase>/models/argmaxinc/whisperkit-coreml/openai_whisper-<variant>`.
final class ModelManager {
    static let modelsDir: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("Voice/models", isDirectory: true)
    }()
    static let repo = "argmaxinc/whisperkit-coreml"
    static let available: [(id: String, label: String)] = [
        ("large-v3-v20240930_turbo_632MB", Strings.modelLabelTurbo),
        ("large-v3-v20240930_turbo", Strings.modelLabelLarge),
    ]
    /// Entries a model folder must contain to count as fully downloaded. `TextDecoderContextPrefill.mlmodelc`
    /// and `generation_config.json` are variant-dependent, so they are deliberately not required here.
    static let requiredEntries = ["config.json", "MelSpectrogram.mlmodelc", "AudioEncoder.mlmodelc", "TextDecoder.mlmodelc"]

    /// `base` defaults to the production models directory; overridable so tests can point at a temp directory.
    func folder(for id: String, base: URL = ModelManager.modelsDir) -> URL {
        base.appendingPathComponent("models/\(Self.repo)/openai_whisper-\(id)")  // layout observed in docs/SPIKES.md
    }

    /// True only when the folder exists and contains every entry in `requiredEntries` — a bare, empty,
    /// or partially-written folder (e.g. an interrupted download) must not be reported as downloaded.
    func isDownloaded(_ id: String, base: URL = ModelManager.modelsDir) -> Bool {
        let dir = folder(for: id, base: base)
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: dir.path, isDirectory: &isDir), isDir.boolValue else { return false }
        return Self.requiredEntries.allSatisfy { FileManager.default.fileExists(atPath: dir.appendingPathComponent($0).path) }
    }

    func delete(_ id: String) throws { try FileManager.default.removeItem(at: folder(for: id)) }

    /// Downloads if needed and returns the model folder. Progress 0…1.
    func ensure(_ id: String, progress: @escaping (Double) -> Void) async throws -> URL {
        if isDownloaded(id) { return folder(for: id) }
        try FileManager.default.createDirectory(at: Self.modelsDir, withIntermediateDirectories: true)
        return try await WhisperKit.download(variant: id, downloadBase: Self.modelsDir, useBackgroundSession: false, from: Self.repo) { p in
            progress(p.fractionCompleted)
        }
    }
}

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
        ("large-v3-v20240930_turbo_632MB", "Large v3 Turbo (compressed, 630 MB) — recommended"),
        ("large-v3-v20240930_turbo", "Large v3 Turbo (full, ~1.5 GB) — maximum accuracy"),
    ]

    func folder(for id: String) -> URL {
        Self.modelsDir.appendingPathComponent("models/\(Self.repo)/openai_whisper-\(id)")  // layout observed in docs/SPIKES.md
    }
    func isDownloaded(_ id: String) -> Bool { FileManager.default.fileExists(atPath: folder(for: id).path) }
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

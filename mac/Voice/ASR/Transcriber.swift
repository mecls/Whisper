import Foundation

struct TranscribeHint { var language: String?; var vocabulary: [String] }
struct Transcript { var text: String; var language: String; var durationMs: Int }

protocol Transcriber {
    var isReady: Bool { get }
    func prepare(progress: @escaping (Double) -> Void) async throws
    func transcribe(_ samples: [Float], hint: TranscribeHint, progress: ((Double) -> Void)?) async throws -> Transcript
}

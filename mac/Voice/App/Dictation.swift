import Foundation

struct FrontmostApp: Equatable {
    let bundleId: String?
    let name: String?
}

enum FallbackReason: String, Codable, Equatable {
    case clientTimeout = "client-timeout", offline, unauthorized, server
    case llmTimeout = "llm-timeout", llmError = "llm-error", llmBusy = "llm-busy", llmTruncated = "llm-truncated", guardRejected = "guard-rejected"
}

enum Injected: String, Codable, Equatable { case cleaned, raw, none, clipboard }

enum DictationStage: Equatable { case recording, transcribing, refining, readyToInsert, inserting }

struct Dictation: Equatable {
    let clientId: UUID
    let startedAt: Date
    var app: FrontmostApp?
    var stage: DictationStage = .recording
    var samples: [Float] = []
    var audioMs: Int = 0
    var raw: String?
    var language: String?
    var asrMs: Int = 0
    var cleaned: String?
    var fallback: FallbackReason?
    var injected: Injected?

    /// What gets pasted: the cleanup when there is one, the raw transcript otherwise.
    var textToInsert: String? { cleaned ?? raw }

    static func == (a: Dictation, b: Dictation) -> Bool { a.clientId == b.clientId && a.stage == b.stage }
}

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

    /// Cleanup that did not happen on `/v1/refine`: `"skipped"` when the gate decided the
    /// transcript needed nothing. Left nil when the server cleaned it — the server records its own
    /// model and would only have to reconcile two answers. There is no client-side `llmMs` to go
    /// with it: cleanup is server-only by decision (spec §7 2a), so the only engine that can time
    /// itself is the one already writing the row.
    var llmModel: String?

    /// How many confirmed streaming segments produced `raw`. 1 for a one-pass transcription, which
    /// has no chunk boundaries. Above 1 the skip gate refuses to skip: skipping is the only path
    /// where nothing inspects the text before it reaches the user's document, so it is the one
    /// place a boundary artifact could land uncorrected.
    var streamedSegments: Int = 1

    /// What gets pasted: the cleanup when there is one, the raw transcript otherwise.
    var textToInsert: String? { cleaned ?? raw }

    static func == (a: Dictation, b: Dictation) -> Bool { a.clientId == b.clientId && a.stage == b.stage }
}

import Foundation

struct OutboxEntry {
    // G2: optional — literal-mode dictations carry no fallback reason at all (nil, not `.offline`).
    let clientId: UUID; let raw: String; let injected: Injected; let fallback: FallbackReason?; let createdAt: Date
    let mode: String; let languageSetting: String; let languageDetected: String?; let app: FrontmostApp?; let audioMs: Int; let asrMs: Int
    // prd-sub-second-dictation.md rule 18: an offline dictation still has to contribute its
    // measurement once connectivity returns, so the timing rides the queued entry rather than
    // being recomputed at replay time — by then the original release is long gone.
    var cleaned: String? = nil; var llmMs: Int? = nil; var llmModel: String? = nil; var totalMs: Int? = nil
}

/// Memory only (no transcript ever touches disk). Capped; oldest dropped first.
/// G5: main-actor — constructed and used only from the @MainActor Coordinator/RefineService.
@MainActor
final class Outbox {
    static let cap = 200
    private var entries: [OutboxEntry] = []
    var count: Int { entries.count }
    func add(_ e: OutboxEntry) { entries.append(e); if entries.count > Self.cap { entries.removeFirst(entries.count - Self.cap) } }
    func drain() -> [OutboxEntry] { defer { entries.removeAll() }; return entries }
    func requeue(_ es: [OutboxEntry]) { for e in es { add(e) } }
}

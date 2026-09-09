import Foundation

struct OutboxEntry {
    let clientId: UUID; let raw: String; let injected: Injected; let fallback: FallbackReason; let createdAt: Date
    let mode: String; let languageSetting: String; let languageDetected: String?; let app: FrontmostApp?; let audioMs: Int; let asrMs: Int
}

/// Memory only (no transcript ever touches disk). Capped; oldest dropped first.
final class Outbox {
    static let cap = 200
    private var entries: [OutboxEntry] = []
    var count: Int { entries.count }
    func add(_ e: OutboxEntry) { entries.append(e); if entries.count > Self.cap { entries.removeFirst(entries.count - Self.cap) } }
    func drain() -> [OutboxEntry] { defer { entries.removeAll() }; return entries }
    func requeue(_ es: [OutboxEntry]) { for e in es { add(e) } }
}

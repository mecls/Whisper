import Foundation
import os

private let log = Logger(subsystem: "co.miraside.voice", category: "refine")

/// Budgeted refine with raw fallback; outbox replay after any successful call.
/// G5: main-actor — the `Refiner` protocol's async methods are called from the @MainActor Coordinator.
@MainActor
final class RefineService: Refiner {
    private let api: VoiceAPIClient
    private let outbox: Outbox
    private var pendingByClientId: [UUID: (Dictation, FallbackReason?)] = [:]
    var asrModel = Preferences.modelId   // C1: Preferences is a static namespace

    init(api: VoiceAPIClient, outbox: Outbox) { self.api = api; self.outbox = outbox }

    func refine(_ d: Dictation, mode: String) async -> RefineResult {
        guard let raw = d.raw else { return .rawFallback(.server) }
        let ctx = RefineRequest.Context(appBundleId: d.app?.bundleId, appName: d.app?.name)
        let timing = RefineRequest.Timing(audioMs: d.audioMs, asrMs: d.asrMs)
        let createdAt = Int(d.startedAt.timeIntervalSince1970 * 1000)
        let languageSetting = Preferences.language

        if mode == "literal" {
            await logOnly(d, injected: .raw, fallback: nil, mode: mode)
            return .literal
        }
        let budget = Budget.ms(rawChars: raw.count)
        let body = RefineRequest(clientId: d.clientId.uuidString.lowercased(), raw: raw, mode: mode, languageSetting: languageSetting, languageDetected: d.language,
                                 budgetMs: budget, context: ctx, timing: timing, asrModel: asrModel, clientVersion: VoiceAPI.version, createdAt: createdAt)
        do {
            let r = try await api.refine(body, budgetMs: budget)
            await replayOutbox()
            if let reason = r.fallbackReason { return .rawFallback(reason) }
            return r.cleaned.isEmpty ? .rawFallback(.guardRejected) : .cleaned(r.cleaned)
        } catch let e as APIError {
            let reason: FallbackReason = switch e {
            case .timeout: .clientTimeout
            case .offline: .offline
            case .unauthorized: .unauthorized
            case .server, .decoding: .server
            }
            pendingByClientId[d.clientId] = (d, reason)
            log.info("refine fallback \(reason.rawValue, privacy: .public)")
            return .rawFallback(reason)
        } catch {
            pendingByClientId[d.clientId] = (d, .server)
            return .rawFallback(.server)
        }
    }

    func reportInjected(clientId: UUID, injected: Injected) async {
        if let (d, reason) = pendingByClientId.removeValue(forKey: clientId) {
            // The refine never completed on our side: log it (or queue it) instead of patching.
            await logOnly(d, injected: injected, fallback: reason, mode: "clean")
            return
        }
        // C3: PATCH 404s until a refine/dictation POST has stored the row for this clientId — this
        // only runs after a successful refine (the branch above handles the "never stored" case), so a
        // 404 here means the earlier POST itself failed; log it, do not retry.
        // G6: the error kind is useful (offline vs. 404 vs. decode failure); APIError carries no
        // transcript text, so it's fine as .public. clientId is a UUID, also fine as .public.
        do { try await api.patchInjected(clientId: clientId, injected: injected) } catch { log.info("patch injected failed: \(String(describing: error), privacy: .public)") }
    }

    private func logOnly(_ d: Dictation, injected: Injected, fallback: FallbackReason?, mode: String) async {
        // G2: pass `fallback` through as-is — literal mode calls this with `nil`, and that must reach
        // the server as `fallbackReason: null`, not get coalesced into a false ".offline".
        let entry = OutboxEntry(clientId: d.clientId, raw: d.raw ?? "", injected: injected, fallback: fallback, createdAt: d.startedAt,
                                mode: mode, languageSetting: Preferences.language, languageDetected: d.language, app: d.app, audioMs: d.audioMs, asrMs: d.asrMs)
        do { try await api.postDictation(request(for: entry)) } catch { outbox.add(entry) }
    }

    private func replayOutbox() async {
        let entries = outbox.drain()
        var failed: [OutboxEntry] = []
        for e in entries { do { try await api.postDictation(request(for: e)) } catch { failed.append(e) } }
        outbox.requeue(failed)
    }

    private func request(for e: OutboxEntry) -> DictationRequest {
        DictationRequest(clientId: e.clientId.uuidString.lowercased(), raw: e.raw, injected: e.injected, fallbackReason: e.fallback, mode: e.mode,
                         languageSetting: e.languageSetting, languageDetected: e.languageDetected,
                         context: .init(appBundleId: e.app?.bundleId, appName: e.app?.name), timing: .init(audioMs: e.audioMs, asrMs: e.asrMs),
                         asrModel: asrModel, clientVersion: VoiceAPI.version, createdAt: Int(e.createdAt.timeIntervalSince1970 * 1000))
    }
}

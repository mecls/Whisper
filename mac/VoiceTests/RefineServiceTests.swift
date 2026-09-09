import XCTest
@testable import Voice

/// C2: `offline` forces every call to throw `.offline` regardless of `refineResult`, independent of
/// whatever success/failure the individual stub properties are set to — this is what lets the offline
/// test flip the whole stub from "server unreachable" to "server reachable" with one bool.
private final class StubAPI: VoiceAPIClient {
    var refineResult: Result<RefineResponse, Error> = .failure(APIError.offline)
    var posted: [DictationRequest] = []
    var patched: [(UUID, Injected)] = []
    var delay: UInt64 = 0
    var offline = false
    func refine(_ body: RefineRequest, budgetMs: Int) async throws -> RefineResponse {
        if offline { throw APIError.offline }
        if delay > 0 { try await Task.sleep(nanoseconds: delay) }
        return try refineResult.get()
    }
    func postDictation(_ body: DictationRequest) async throws {
        if offline { throw APIError.offline }
        posted.append(body)
    }
    func patchInjected(clientId: UUID, injected: Injected) async throws {
        if offline { throw APIError.offline }
        patched.append((clientId, injected))
    }
    func me() async throws -> MeResponse { throw APIError.offline }
    func putSettings(_ s: ServerSettings) async throws {}
}

final class RefineServiceTests: XCTestCase {
    private func dictation(_ raw: String) -> Dictation {
        var d = Dictation(clientId: UUID(), startedAt: Date(), app: nil); d.raw = raw; d.audioMs = 3000; d.asrMs = 900; d.language = "pt"; return d
    }

    func testCleanedResponse() async {
        let api = StubAPI()
        api.refineResult = .success(RefineResponse(clientId: "x", cleaned: "Olá.", raw: "olá", model: "gemma4", llmMs: 500, fallbackReason: nil))
        let s = RefineService(api: api, outbox: Outbox())
        let r = await s.refine(dictation("olá"), mode: "clean")
        XCTAssertEqual(r, .cleaned("Olá."))
    }

    func testServerSideFallbackPastesServerRaw() async {
        let api = StubAPI()
        api.refineResult = .success(RefineResponse(clientId: "x", cleaned: "olá", raw: "olá", model: nil, llmMs: 0, fallbackReason: .llmBusy))
        let s = RefineService(api: api, outbox: Outbox())
        let r = await s.refine(dictation("olá"), mode: "clean")
        XCTAssertEqual(r, .rawFallback(.llmBusy))
    }

    // C2: the brief's version of this test cannot pass as written (refineResult alone cannot model
    // "offline"). Uses StubAPI.offline instead, and reportInjected targets the SAME dictation's
    // clientId (the one refine() actually saw), matching how the real pendingByClientId map is keyed.
    func testOfflineQueuesToOutboxAndReplaysLater() async {
        let api = StubAPI(); let outbox = Outbox()
        let s = RefineService(api: api, outbox: outbox)
        api.offline = true
        let d = dictation("olá")
        let r1 = await s.refine(d, mode: "clean")
        XCTAssertEqual(r1, .rawFallback(.offline))
        await s.reportInjected(clientId: d.clientId, injected: .raw)   // offline: cannot patch
        XCTAssertEqual(outbox.count, 1)
        api.offline = false
        api.refineResult = .success(RefineResponse(clientId: "x", cleaned: "Olá.", raw: "olá", model: "gemma4", llmMs: 1, fallbackReason: nil))
        _ = await s.refine(dictation("olá"), mode: "clean")
        XCTAssertEqual(api.posted.count, 1)
        XCTAssertEqual(outbox.count, 0)
    }

    func testLiteralSkipsNetworkButStillLogs() async {
        let api = StubAPI()
        let s = RefineService(api: api, outbox: Outbox())
        let r = await s.refine(dictation("olá"), mode: "literal")
        XCTAssertEqual(r, .literal)
        XCTAssertEqual(api.posted.count, 1)
    }

    func testUnauthorizedMapsToUnauthorized() async {
        let api = StubAPI(); api.refineResult = .failure(APIError.unauthorized)
        let s = RefineService(api: api, outbox: Outbox())
        let r = await s.refine(dictation("olá"), mode: "clean")
        XCTAssertEqual(r, .rawFallback(.unauthorized))
    }
}

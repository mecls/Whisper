import XCTest
@testable import Voice

// G5: RefineService is @MainActor; the test class is too so it can construct/call it synchronously.
@MainActor
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
        // G2: the replayed entry is the offline dictation's — it must carry the .offline reason it
        // actually failed with, not nil and not some other value.
        XCTAssertEqual(api.posted.first?.fallbackReason, .offline)
    }

    func testLiteralSkipsNetworkButStillLogs() async {
        let api = StubAPI()
        let s = RefineService(api: api, outbox: Outbox())
        let r = await s.refine(dictation("olá"), mode: "literal")
        XCTAssertEqual(r, .literal)
        XCTAssertEqual(api.posted.count, 1)
        // G2: literal mode is a user choice, not a fallback — the posted row must carry
        // fallbackReason: null, and the injected kind it was actually logged with.
        XCTAssertNil(api.posted.first?.fallbackReason)
        XCTAssertEqual(api.posted.first?.injected, .raw)
    }

    func testUnauthorizedMapsToUnauthorized() async {
        let api = StubAPI(); api.refineResult = .failure(APIError.unauthorized)
        let s = RefineService(api: api, outbox: Outbox())
        let r = await s.refine(dictation("olá"), mode: "clean")
        XCTAssertEqual(r, .rawFallback(.unauthorized))
    }
}

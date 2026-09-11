import Foundation
@testable import Voice

/// Shared test double for `VoiceAPIClient`. `offline` forces every call to throw `.offline`
/// regardless of the individual `*Result` properties — this is what lets a test flip the whole stub
/// from "server unreachable" to "server reachable" with one bool.
final class StubAPI: VoiceAPIClient {
    var refineResult: Result<RefineResponse, Error> = .failure(APIError.offline)
    var meResult: Result<MeResponse, Error> = .failure(APIError.offline)
    var posted: [DictationRequest] = []
    var patched: [(UUID, Injected)] = []
    var patchedTotalMs: [Int?] = []
    var prewarms = 0
    var puts: [ServerSettings] = []
    var delay: UInt64 = 0
    var offline = false
    // D3: in-memory dictionary for listTerms/addTerm/deleteTerm.
    var terms: [DictionaryEntry] = []

    func refine(_ body: RefineRequest, budgetMs: Int) async throws -> RefineResponse {
        if offline { throw APIError.offline }
        if delay > 0 { try await Task.sleep(nanoseconds: delay) }
        return try refineResult.get()
    }
    func postDictation(_ body: DictationRequest) async throws {
        if offline { throw APIError.offline }
        posted.append(body)
    }
    func patchInjected(clientId: UUID, injected: Injected, totalMs: Int?) async throws {
        if offline { throw APIError.offline }
        patched.append((clientId, injected)); patchedTotalMs.append(totalMs)
    }
    func prewarm() { prewarms += 1 }
    func me() async throws -> MeResponse {
        if offline { throw APIError.offline }
        return try meResult.get()
    }
    func putSettings(_ s: ServerSettings) async throws {
        if offline { throw APIError.offline }
        puts.append(s)
    }
    func listTerms() async throws -> [DictionaryEntry] {
        if offline { throw APIError.offline }
        return terms
    }
    func addTerm(_ term: String, replacement: String?, teamWide: Bool) async throws -> DictionaryEntry {
        if offline { throw APIError.offline }
        let entry = DictionaryEntry(id: UUID().uuidString, term: term, replacement: replacement, note: nil, teamWide: teamWide, createdAt: 0)
        terms.append(entry)
        return entry
    }
    func deleteTerm(id: String) async throws {
        if offline { throw APIError.offline }
        terms.removeAll { $0.id == id }
    }
}

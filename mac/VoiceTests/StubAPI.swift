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
    var puts: [ServerSettings] = []
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
    func me() async throws -> MeResponse {
        if offline { throw APIError.offline }
        return try meResult.get()
    }
    func putSettings(_ s: ServerSettings) async throws {
        if offline { throw APIError.offline }
        puts.append(s)
    }
}

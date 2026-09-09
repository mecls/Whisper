import Foundation

struct RefineRequest: Encodable {
    let clientId: String, raw: String, mode: String, languageSetting: String, languageDetected: String?, budgetMs: Int
    let context: Context, timing: Timing, asrModel: String, clientVersion: String, createdAt: Int
    struct Context: Encodable { let appBundleId: String?; let appName: String? }
    struct Timing: Encodable { let audioMs: Int; let asrMs: Int }
}
struct RefineResponse: Decodable, Equatable {
    let clientId: String; let cleaned: String; let raw: String; let model: String?; let llmMs: Int; let fallbackReason: FallbackReason?
}
struct DictationRequest: Encodable {
    let clientId: String, raw: String, injected: Injected, fallbackReason: FallbackReason?, mode: String, languageSetting: String, languageDetected: String?
    let context: RefineRequest.Context, timing: RefineRequest.Timing, asrModel: String, clientVersion: String, createdAt: Int
}

/// C3: `llmModel` must be OMITTED from the encoded body to clear a server-side override — sending
/// `llmModel: null` is a 400 validation error, not a way to clear it. Swift's synthesized Encodable
/// would encode `null` for a nil Optional, so encoding is hand-written with `encodeIfPresent`.
struct ServerSettings: Codable, Equatable {
    var mode: String; var language: String; var hotkey: String; var llmModel: String?

    enum CodingKeys: String, CodingKey { case mode, language, hotkey, llmModel }

    init(mode: String, language: String, hotkey: String, llmModel: String? = nil) {
        self.mode = mode; self.language = language; self.hotkey = hotkey; self.llmModel = llmModel
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        mode = try c.decode(String.self, forKey: .mode)
        language = try c.decode(String.self, forKey: .language)
        hotkey = try c.decode(String.self, forKey: .hotkey)
        llmModel = try c.decodeIfPresent(String.self, forKey: .llmModel)
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(mode, forKey: .mode)
        try c.encode(language, forKey: .language)
        try c.encode(hotkey, forKey: .hotkey)
        try c.encodeIfPresent(llmModel, forKey: .llmModel)
    }
}

struct MeResponse: Decodable {
    struct User: Decodable { let id: String; let name: String }
    struct Term: Decodable { let term: String; let replacement: String? }
    /// C3: server gained `allowedModels: [String]` after the plan was written. Decoded
    /// tolerantly (optional-with-default) so an older server payload without the field still parses.
    struct Server: Decodable {
        let version: String; let model: String; let concurrency: Int; let allowedModels: [String]
        enum CodingKeys: String, CodingKey { case version, model, concurrency, allowedModels }
        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            version = try c.decode(String.self, forKey: .version)
            model = try c.decode(String.self, forKey: .model)
            concurrency = try c.decode(Int.self, forKey: .concurrency)
            allowedModels = try c.decodeIfPresent([String].self, forKey: .allowedModels) ?? []
        }
    }
    let user: User; let settings: ServerSettings; let dictionary: [Term]; let server: Server
}

enum APIError: Error, Equatable { case unauthorized, server(Int), offline, timeout, decoding }

protocol VoiceAPIClient {
    func refine(_ body: RefineRequest, budgetMs: Int) async throws -> RefineResponse
    func postDictation(_ body: DictationRequest) async throws
    func patchInjected(clientId: UUID, injected: Injected) async throws
    func me() async throws -> MeResponse
    func putSettings(_ s: ServerSettings) async throws
}

final class VoiceAPI: VoiceAPIClient {
    private let base: URL
    private let tokenProvider: () -> String?
    private let session: URLSession
    static let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0"

    init(base: URL, tokenProvider: @escaping () -> String?) {
        self.base = base; self.tokenProvider = tokenProvider
        let c = URLSessionConfiguration.ephemeral
        c.waitsForConnectivity = false
        c.timeoutIntervalForRequest = 10
        session = URLSession(configuration: c)
    }

    // C3: X-Request-Id correlates the server's log line to a dictation on /v1/refine and /v1/dictations only.
    func refine(_ body: RefineRequest, budgetMs: Int) async throws -> RefineResponse {
        try await send("POST", "/v1/refine", body: body, timeout: Double(budgetMs) / 1000, requestId: body.clientId)
    }
    func postDictation(_ body: DictationRequest) async throws {
        let _: Empty = try await send("POST", "/v1/dictations", body: body, timeout: 10, requestId: body.clientId)
    }
    func patchInjected(clientId: UUID, injected: Injected) async throws {
        let _: Empty = try await send("PATCH", "/v1/dictations/by-client/\(clientId.uuidString.lowercased())", body: ["injected": injected.rawValue], timeout: 10)
    }
    func me() async throws -> MeResponse { try await send("GET", "/v1/me", body: nil as Empty?, timeout: 10) }
    func putSettings(_ s: ServerSettings) async throws { let _: ServerSettings = try await send("PUT", "/v1/settings", body: s, timeout: 10) }

    private struct Empty: Codable {}

    private func send<B: Encodable, R: Decodable>(_ method: String, _ path: String, body: B?, timeout: TimeInterval, requestId: String? = nil) async throws -> R {
        var req = URLRequest(url: base.appendingPathComponent(path), timeoutInterval: timeout)
        req.httpMethod = method
        req.setValue("application/json", forHTTPHeaderField: "content-type")
        req.setValue("miraside-voice/\(Self.version)", forHTTPHeaderField: "user-agent")
        if let requestId { req.setValue(requestId, forHTTPHeaderField: "X-Request-Id") }
        if let t = tokenProvider() { req.setValue("Bearer \(t)", forHTTPHeaderField: "authorization") }
        if let body { req.httpBody = try JSONEncoder().encode(body) }
        let (data, resp): (Data, URLResponse)
        do { (data, resp) = try await session.data(for: req) } catch let e as URLError {
            switch e.code {
            case .timedOut: throw APIError.timeout
            case .notConnectedToInternet, .cannotConnectToHost, .cannotFindHost, .networkConnectionLost, .dnsLookupFailed: throw APIError.offline
            default: throw APIError.server(-1)
            }
        }
        let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
        if status == 401 { throw APIError.unauthorized }
        // C3: 429 (either rate-limit body shape) and any 5xx both fall through to this generic
        // status-keyed branch — never decode the body to distinguish them.
        guard (200..<300).contains(status) else { throw APIError.server(status) }
        if R.self == Empty.self { return Empty() as! R }
        do { return try JSONDecoder().decode(R.self, from: data) } catch { throw APIError.decoding }
    }
}

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
    /// Cleanup that did not happen on `/v1/refine`, and the release→paste measurement. The server
    /// stored literal nulls for the first three until rule 16, which meant a dictation the client
    /// cleaned — or deliberately did not clean — lost its text and all of its timings.
    var cleaned: String? = nil, llmMs: Int? = nil, llmModel: String? = nil, totalMs: Int? = nil
}

/// PATCH body: the injection outcome, plus the measurement when there is one. A nil `totalMs`
/// encodes as an explicit `null` (synthesized Encodable, same as the C3 note below), which the
/// server's `nullish()` accepts and treats as "not measured" — leaving any stored value alone.
struct InjectedRequest: Encodable {
    let injected: Injected
    var totalMs: Int? = nil
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

/// D3/`docs/API.md`: the full `GET/POST /v1/dictionary` entry shape — richer than `MeResponse.Term`
/// (which only carries `term`/`replacement`, the subset embedded in `/v1/me`). `id` is required to
/// delete an entry, which `/v1/me`'s reduced shape cannot provide.
struct DictionaryEntry: Decodable, Identifiable, Equatable {
    let id: String
    let term: String
    let replacement: String?
    let note: String?
    let teamWide: Bool
    let createdAt: Int
}

enum APIError: Error, Equatable { case unauthorized, server(Int), offline, timeout, decoding }

protocol VoiceAPIClient {
    func refine(_ body: RefineRequest, budgetMs: Int) async throws -> RefineResponse
    func postDictation(_ body: DictationRequest) async throws
    func patchInjected(clientId: UUID, injected: Injected, totalMs: Int?) async throws
    func prewarm()
    func insights(tz: String, weeks: Int) async throws -> Insights
    func me() async throws -> MeResponse
    func putSettings(_ s: ServerSettings) async throws
    // D3: Task 9's Dictionary tab.
    func listTerms() async throws -> [DictionaryEntry]
    func addTerm(_ term: String, replacement: String?, teamWide: Bool) async throws -> DictionaryEntry
    func deleteTerm(id: String) async throws
}

final class VoiceAPI: VoiceAPIClient {
    private let base: URL
    // G4: Sendable so it can be handed to a detached task — reading the Keychain can pop the
    // system "allow access" dialog, which must never block the calling (often @MainActor) thread.
    private let tokenProvider: @Sendable () -> String?
    private let session: URLSession
    static let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0"

    init(base: URL, tokenProvider: @escaping @Sendable () -> String?) {
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
    func patchInjected(clientId: UUID, injected: Injected, totalMs: Int?) async throws {
        let _: Empty = try await send("PATCH", "/v1/dictations/by-client/\(clientId.uuidString.lowercased())",
                                      body: InjectedRequest(injected: injected, totalMs: totalMs), timeout: 10)
    }
    /// Rules 19-21: open the connection while the user is still speaking, so the TLS handshake is
    /// not sitting in the critical path between release and paste. Fire-and-forget by construction
    /// — no await, no result, no error surface. `/health` needs no token, which keeps the Keychain
    /// (and its possible "allow access" dialog) off the hot path entirely.
    func prewarm() {
        var r = URLRequest(url: base.appendingPathComponent("health"))
        r.httpMethod = "HEAD"
        r.timeoutInterval = 3
        session.dataTask(with: r) { _, _, _ in }.resume()
    }

    /// The dashboard's only call. `tz` is the Mac's current zone, so the day buckets match the
    /// calendar the user actually lives in; the server rejects a zone it cannot format with rather
    /// than falling back to UTC and handing back a plausible wrong streak.
    func insights(tz: String, weeks: Int) async throws -> Insights {
        try await send("GET", "/v1/insights", body: nil as Empty?, timeout: 10,
                       query: [URLQueryItem(name: "tz", value: tz), URLQueryItem(name: "weeks", value: String(weeks))])
    }

    func me() async throws -> MeResponse { try await send("GET", "/v1/me", body: nil as Empty?, timeout: 10) }
    func putSettings(_ s: ServerSettings) async throws { let _: ServerSettings = try await send("PUT", "/v1/settings", body: s, timeout: 10) }

    // D3: mirrors GET/POST/DELETE /v1/dictionary (docs/API.md).
    func listTerms() async throws -> [DictionaryEntry] {
        try await send("GET", "/v1/dictionary", body: nil as Empty?, timeout: 10)
    }
    func addTerm(_ term: String, replacement: String?, teamWide: Bool) async throws -> DictionaryEntry {
        struct Body: Encodable { let term: String; let replacement: String?; let teamWide: Bool }
        return try await send("POST", "/v1/dictionary", body: Body(term: term, replacement: replacement, teamWide: teamWide), timeout: 10)
    }
    func deleteTerm(id: String) async throws {
        let _: Empty = try await send("DELETE", "/v1/dictionary/\(id)", body: nil as Empty?, timeout: 10)
    }

    private struct Empty: Codable {}

    private func send<B: Encodable, R: Decodable>(_ method: String, _ path: String, body: B?, timeout: TimeInterval, requestId: String? = nil, query: [URLQueryItem] = []) async throws -> R {
        // G4: resolve off the calling actor before building the request.
        let token = await Task.detached(priority: .userInitiated) { [tokenProvider] in tokenProvider() }.value
        // Built through URLComponents rather than appended to the path: `appendingPathComponent`
        // percent-encodes `?` and `&`, which would turn a query into a nonsense path segment.
        var url = base.appendingPathComponent(path)
        if !query.isEmpty, var comps = URLComponents(url: url, resolvingAgainstBaseURL: false) {
            comps.queryItems = query
            url = comps.url ?? url
        }
        var req = URLRequest(url: url, timeoutInterval: timeout)
        req.httpMethod = method
        req.setValue("application/json", forHTTPHeaderField: "content-type")
        req.setValue("miraside-voice/\(Self.version)", forHTTPHeaderField: "user-agent")
        if let requestId { req.setValue(requestId, forHTTPHeaderField: "X-Request-Id") }
        if let token { req.setValue("Bearer \(token)", forHTTPHeaderField: "authorization") }
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

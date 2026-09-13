using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spit.Core;

// Port of the request/response types and the client protocol in mac/Voice/Refine/VoiceAPI.swift.
// Shapes are the server's (docs/API.md); the server needs no change for Windows (rule 45).

/// The one serializer configuration every request and response goes through. Nulls are omitted,
/// which is what Swift's synthesized Encodable does for optionals — and what `PUT /v1/settings`
/// requires: `llmModel: null` is a 400, only an omitted `llmModel` clears the override.
public static class SpitJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record RefineContext(string? AppBundleId, string? AppName);

public sealed record RefineTiming(int AudioMs, int AsrMs);

public sealed record RefineRequest(
    string ClientId,
    string Raw,
    string Mode,
    string LanguageSetting,
    string? LanguageDetected,
    int BudgetMs,
    RefineContext Context,
    RefineTiming Timing,
    string AsrModel,
    string ClientVersion,
    long CreatedAt);

public sealed record RefineResponse(
    string ClientId,
    string Cleaned,
    string Raw,
    string? Model,
    int LlmMs,
    FallbackReason? FallbackReason);

public sealed record DictationRequest(
    string ClientId,
    string Raw,
    Injected Injected,
    FallbackReason? FallbackReason,
    string Mode,
    string LanguageSetting,
    string? LanguageDetected,
    RefineContext Context,
    RefineTiming Timing,
    string AsrModel,
    string ClientVersion,
    long CreatedAt)
{
    /// Cleanup that did not happen on `/v1/refine`, and the release→paste measurement.
    public string? Cleaned { get; init; }
    public int? LlmMs { get; init; }
    public string? LlmModel { get; init; }
    public int? TotalMs { get; init; }
}

/// PATCH body: the injection outcome, plus the measurement when there is one.
public sealed record InjectedRequest(Injected Injected, int? TotalMs);

/// `hotkey` is one of the server's Mac values (`fn` | `rightOption` | `rightCommand`). The Windows
/// client never interprets it and echoes it back unchanged (rule 46).
public sealed record ServerSettings(string Mode, string Language, string Hotkey, string? LlmModel = null);

public sealed record MeUser(string Id, string Name);

public sealed record MeTerm(string Term, string? Replacement);

public sealed record MeServer(string Version, string Model, int Concurrency, IReadOnlyList<string>? AllowedModels)
{
    /// An older server payload without the field still parses (Mac C3).
    public IReadOnlyList<string> AllowedModelsOrEmpty => AllowedModels ?? [];
}

public sealed record MeResponse(MeUser User, ServerSettings Settings, IReadOnlyList<MeTerm> Dictionary, MeServer Server);

/// The full `GET/POST /v1/dictionary` entry — `Id` is required to delete one.
public sealed record DictionaryEntry(string Id, string Term, string? Replacement, string? Note, bool TeamWide, long CreatedAt);

public enum ApiErrorKind { Unauthorized, Server, Offline, Timeout, Decoding }

/// Port of `APIError`. `Status` is the HTTP status for `Server`, -1 for a transport error that is
/// neither offline nor a timeout, and 0 otherwise.
public sealed class ApiException(ApiErrorKind kind, int status = 0)
    : Exception(kind == ApiErrorKind.Server ? $"server({status})" : kind.ToString().ToLowerInvariant())
{
    public ApiErrorKind Kind { get; } = kind;
    public int Status { get; } = status;

    public static ApiException Unauthorized() => new(ApiErrorKind.Unauthorized);
    public static ApiException Offline() => new(ApiErrorKind.Offline);
    public static ApiException Timeout() => new(ApiErrorKind.Timeout);
    public static ApiException Decoding() => new(ApiErrorKind.Decoding);
    public static ApiException ServerStatus(int status) => new(ApiErrorKind.Server, status);
}

/// Port of the `VoiceAPIClient` protocol. Every failure is an `ApiException`.
public interface IVoiceApiClient
{
    Task<RefineResponse> RefineAsync(RefineRequest body, int budgetMs);
    Task PostDictationAsync(DictationRequest body);
    Task PatchInjectedAsync(Guid clientId, Injected injected, int? totalMs);

    /// Fire-and-forget `HEAD /health` (3 s timeout); never throws, never awaited by callers.
    void Prewarm();

    Task<Insights> InsightsAsync(string tz, int weeks);
    Task<MeResponse> MeAsync();
    Task PutSettingsAsync(ServerSettings settings);
    Task<IReadOnlyList<DictionaryEntry>> ListTermsAsync();
    Task<DictionaryEntry> AddTermAsync(string term, string? replacement, bool teamWide);
    Task DeleteTermAsync(string id);
}

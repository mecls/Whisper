using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace Spit.Core;

/// The real `IVoiceApiClient`: port of `VoiceAPI` in mac/Voice/Refine/VoiceAPI.swift over `HttpClient`.
/// Every body goes through `SpitJson.Options` (camelCase, nulls omitted). Every failure is an
/// `ApiException`, mapped exactly as the Mac maps `URLError` and HTTP status.
public sealed class HttpVoiceApiClient : IVoiceApiClient, IDisposable
{
    /// `VoiceAPI`'s `timeoutIntervalForRequest` (rule 22). `/v1/refine` uses the budget instead.
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// `HEAD /health` pre-warm timeout (rule 22).
    public static readonly TimeSpan PrewarmTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly Func<string?> _tokenProvider;
    private readonly string _userAgent;

    public HttpVoiceApiClient(Uri baseUrl, Func<string?> tokenProvider, string clientVersion, HttpMessageHandler? handler = null)
    {
        _base = baseUrl.AbsoluteUri.TrimEnd('/');
        _tokenProvider = tokenProvider;
        _userAgent = $"spit-windows/{clientVersion}";
        // Per-request timeouts are cancellation tokens, so the client-wide one is off. A handler passed in
        // (tests) belongs to the caller.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) },
            disposeHandler: handler is null)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    // Mac C3: X-Request-Id correlates the server's log line to a dictation, on refine and dictations only.
    public Task<RefineResponse> RefineAsync(RefineRequest body, int budgetMs) =>
        SendAsync<RefineResponse>(HttpMethod.Post, "/v1/refine", body, TimeSpan.FromMilliseconds(budgetMs), body.ClientId);

    public Task PostDictationAsync(DictationRequest body) =>
        SendNoContentAsync(HttpMethod.Post, "/v1/dictations", body, DefaultTimeout, body.ClientId);

    public Task PatchInjectedAsync(Guid clientId, Injected injected, int? totalMs) =>
        SendNoContentAsync(HttpMethod.Patch, $"/v1/dictations/by-client/{clientId:D}", new InjectedRequest(injected, totalMs), DefaultTimeout);

    /// Rules 19-21 (sub-second): open the connection while the user is still speaking, so the TLS
    /// handshake is not in the critical path between release and paste. Fire-and-forget by construction:
    /// no await, no result, no error surface. `/health` needs no token, which keeps the credential store
    /// off the hot path.
    public void Prewarm() => _ = PrewarmAsync();

    private async Task PrewarmAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(PrewarmTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(_base + "/health"));
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Never surfaces: a failed pre-warm costs nothing but the handshake it failed to save.
        }
    }

    /// `tz` must already be IANA (rule 43): the server rejects a zone it cannot format with rather than
    /// falling back to UTC and handing back a plausible wrong streak.
    public Task<Insights> InsightsAsync(string tz, int weeks) =>
        SendAsync<Insights>(HttpMethod.Get,
            $"/v1/insights?tz={Uri.EscapeDataString(tz)}&weeks={weeks.ToString(CultureInfo.InvariantCulture)}", null, DefaultTimeout);

    public Task<MeResponse> MeAsync() => SendAsync<MeResponse>(HttpMethod.Get, "/v1/me", null, DefaultTimeout);

    /// Decodes the echo, as the Mac does, so a server that stored something else is a decoding error.
    public Task PutSettingsAsync(ServerSettings settings) =>
        SendAsync<ServerSettings>(HttpMethod.Put, "/v1/settings", settings, DefaultTimeout);

    public Task<IReadOnlyList<DictionaryEntry>> ListTermsAsync() =>
        SendAsync<IReadOnlyList<DictionaryEntry>>(HttpMethod.Get, "/v1/dictionary", null, DefaultTimeout);

    public Task<DictionaryEntry> AddTermAsync(string term, string? replacement, bool teamWide) =>
        SendAsync<DictionaryEntry>(HttpMethod.Post, "/v1/dictionary", new AddTermBody(term, replacement, teamWide), DefaultTimeout);

    public Task DeleteTermAsync(string id) =>
        SendNoContentAsync(HttpMethod.Delete, "/v1/dictionary/" + Uri.EscapeDataString(id), null, DefaultTimeout);

    public void Dispose() => _http.Dispose();

    private sealed record AddTermBody(string Term, string? Replacement, bool TeamWide);

    private async Task<T> SendAsync<T>(HttpMethod method, string pathAndQuery, object? body, TimeSpan timeout, string? requestId = null)
    {
        var bytes = await SendRawAsync(method, pathAndQuery, body, timeout, requestId).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, SpitJson.Options) ?? throw ApiException.Decoding();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException)
        {
            throw ApiException.Decoding();
        }
    }

    /// The Mac's `Empty` responses: status checked, body ignored.
    private async Task SendNoContentAsync(HttpMethod method, string pathAndQuery, object? body, TimeSpan timeout, string? requestId = null) =>
        await SendRawAsync(method, pathAndQuery, body, timeout, requestId).ConfigureAwait(false);

    private async Task<byte[]> SendRawAsync(HttpMethod method, string pathAndQuery, object? body, TimeSpan timeout, string? requestId)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var token = _tokenProvider();
            using var request = new HttpRequestMessage(method, new Uri(_base + pathAndQuery));
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            if (requestId is not null) request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
            if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), SpitJson.Options));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 401) throw ApiException.Unauthorized();
            // Mac C3: 429 (either rate-limit body shape) and any 5xx fall through to this status-keyed
            // branch — the body is never decoded to tell them apart.
            if (status is < 200 or >= 300) throw ApiException.ServerStatus(status);
            return await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            throw;
        }
        catch (OperationCanceledException e) when (cts.IsCancellationRequested || e.InnerException is TimeoutException)
        {
            throw ApiException.Timeout();
        }
        catch (Exception e) when (IsOffline(e))
        {
            throw ApiException.Offline();
        }
        catch (Exception)
        {
            throw ApiException.ServerStatus(-1);
        }
    }

    /// The Mac's offline set — not connected, cannot find or connect to the host, DNS failure, connection
    /// lost — as .NET reports it. TLS, protocol and malformed-response failures are `server(-1)`.
    private static bool IsOffline(Exception e) => e switch
    {
        HttpRequestException { StatusCode: null } h => h.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded => true,
            HttpRequestError.Unknown => h.InnerException is SocketException,
            _ => false,
        },
        HttpIOException io => io.HttpRequestError == HttpRequestError.ResponseEnded,
        SocketException => true,
        _ => false,
    };
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Spit.Core.Tests;

/// Windows-only: `HttpVoiceApiClient` against a fake `HttpMessageHandler` — no network.
public sealed class HttpVoiceApiClientTests
{
    private static readonly Uri Base = new("https://voice.example.test");

    /// What the client sent, captured inside the handler (the client disposes the request afterwards).
    private sealed record Sent(HttpMethod Method, Uri Uri, string? Authorization, string? RequestId, string? UserAgent,
        string? ContentType, string? Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            static string? header(HttpRequestMessage r, string name) =>
                r.Headers.TryGetValues(name, out var v) ? string.Join(" ", v) : null;
            Requests.Add(new Sent(request.Method, request.RequestUri!, header(request, "Authorization"), header(request, "X-Request-Id"),
                header(request, "User-Agent"), request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await respond(request, cancellationToken);
        }
    }

    private static FakeHandler returning(HttpStatusCode status, string body = "{}") =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    private static FakeHandler throwing(Exception e) => new((_, _) => Task.FromException<HttpResponseMessage>(e));

    private static HttpVoiceApiClient client(HttpMessageHandler handler, string? token = "mv_test") =>
        new(Base, () => token, "0.2.0", handler);

    private const string MeJson = """
        {"user":{"id":"u1","name":"Miguel"},"settings":{"mode":"clean","language":"auto","hotkey":"fn"},
         "dictionary":[],"server":{"version":"0.1.0","model":"gpt-oss:120b","concurrency":1}}
        """;

    private static RefineRequest refineRequest(string clientId) =>
        new(clientId, "hã olá", "clean", "auto", null, 3500, new RefineContext(null, null), new RefineTiming(3000, 900),
            "whisper.cpp/ggml-large-v3-turbo-q5_0", "0.2.0", 1_757_400_000_000);

    private static async Task<ApiException> failure(Func<Task> call) => await Assert.ThrowsAsync<ApiException>(call);

    [Fact]
    public async Task testAuthorizationHeaderIsSentWhenATokenExists()
    {
        var handler = returning(HttpStatusCode.OK, MeJson);
        using var api = client(handler, token: "mv_abc");
        var me = await api.MeAsync();
        Assert.Equal("Miguel", me.User.Name);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("Bearer mv_abc", sent.Authorization);
        Assert.Equal("spit-windows/0.2.0", sent.UserAgent);
        Assert.Equal("https://voice.example.test/v1/me", sent.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task testNoAuthorizationHeaderWithoutAToken()
    {
        var handler = returning(HttpStatusCode.OK, MeJson);
        using var api = client(handler, token: null);
        await api.MeAsync();
        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task test401MapsToUnauthorized()
    {
        using var api = client(returning(HttpStatusCode.Unauthorized, """{"error":"invalid_token","message":"Authentication failed"}"""));
        Assert.Equal(ApiErrorKind.Unauthorized, (await failure(api.MeAsync)).Kind);
    }

    [Fact]
    public async Task test500MapsToServerStatus()
    {
        using var api = client(returning(HttpStatusCode.InternalServerError, """{"error":"server","message":"Internal error"}"""));
        var e = await failure(api.MeAsync);
        Assert.Equal(ApiErrorKind.Server, e.Kind);
        Assert.Equal(500, e.Status);
    }

    [Fact]
    public async Task testTimeoutMapsToTimeout()
    {
        // /v1/refine's timeout is its budget: a server that never answers is a timeout after budgetMs.
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var api = client(handler);
        var e = await failure(() => api.RefineAsync(refineRequest("11111111-1111-4111-8111-111111111111"), budgetMs: 50));
        Assert.Equal(ApiErrorKind.Timeout, e.Kind);
    }

    [Fact]
    public async Task testConnectionFailuresMapToOffline()
    {
        Exception[] offline =
        [
            new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused", new SocketException((int)SocketError.ConnectionRefused)),
            new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known"),
        ];
        foreach (var error in offline)
        {
            using var api = client(throwing(error));
            Assert.Equal(ApiErrorKind.Offline, (await failure(api.MeAsync)).Kind);
        }
    }

    [Fact]
    public async Task testOtherTransportFailuresMapToServerMinusOne()
    {
        using var api = client(throwing(new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS handshake failed")));
        var e = await failure(api.MeAsync);
        Assert.Equal(ApiErrorKind.Server, e.Kind);
        Assert.Equal(-1, e.Status);
    }

    [Fact]
    public async Task testUndecodableBodyMapsToDecoding()
    {
        using var api = client(returning(HttpStatusCode.OK, "<html>captive portal</html>"));
        Assert.Equal(ApiErrorKind.Decoding, (await failure(api.MeAsync)).Kind);
    }

    [Fact]
    public async Task testLlmModelIsOmittedFromThePutBodyWhenNull()
    {
        // docs/API.md: `llmModel: null` is a 400; only an omitted llmModel clears the override.
        var handler = returning(HttpStatusCode.OK, """{"mode":"literal","language":"pt","hotkey":"rightOption"}""");
        using var api = client(handler);
        await api.PutSettingsAsync(new ServerSettings("literal", "pt", "rightOption"));
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal("application/json", sent.ContentType);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.False(body.RootElement.TryGetProperty("llmModel", out _));
        Assert.Equal("rightOption", body.RootElement.GetProperty("hotkey").GetString());
    }

    [Fact]
    public async Task testInsightsQueryEncodesTheTimeZone()
    {
        var handler = returning(HttpStatusCode.OK,
            """{"totals":{"dictations":0,"words":0,"audioMs":0,"wpm":null},"streak":{"current":0,"longest":0},"days":[],"apps":[],"generatedAt":1}""");
        using var api = client(handler);
        var insights = await api.InsightsAsync("Etc/GMT+3", 21);
        Assert.True(insights.IsEmpty);
        var sent = Assert.Single(handler.Requests);
        // An unencoded '+' would reach the server as a space and name a zone that does not exist.
        Assert.Equal("?tz=Etc%2FGMT%2B3&weeks=21", sent.Uri.Query);
        Assert.Equal("/v1/insights", sent.Uri.AbsolutePath);
        Assert.Null(sent.ContentType);
    }

    [Fact]
    public async Task testRefineCarriesTheClientIdAsXRequestId()
    {
        const string id = "11111111-1111-4111-8111-111111111111";
        var handler = returning(HttpStatusCode.OK,
            $$"""{"clientId":"{{id}}","cleaned":"hã olá","raw":"hã olá","model":null,"llmMs":0,"fallbackReason":"llm-busy"}""");
        using var api = client(handler);
        var r = await api.RefineAsync(refineRequest(id), budgetMs: 3500);
        Assert.Equal(FallbackReason.LlmBusy, r.FallbackReason);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(id, sent.RequestId);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.Equal(1_757_400_000_000, body.RootElement.GetProperty("createdAt").GetInt64());
        Assert.False(body.RootElement.TryGetProperty("languageDetected", out _));   // optional, never null
    }

    [Fact]
    public async Task testPatchUsesTheLowercaseClientIdPath()
    {
        var handler = returning(HttpStatusCode.OK, """{"ok":true}""");
        using var api = client(handler);
        var clientId = Guid.Parse("ABCDEF12-3456-4789-8ABC-DEF123456789");
        await api.PatchInjectedAsync(clientId, Injected.Clipboard, totalMs: null);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, sent.Method);
        Assert.Equal("/v1/dictations/by-client/abcdef12-3456-4789-8abc-def123456789", sent.Uri.AbsolutePath);
        Assert.Null(sent.RequestId);
        Assert.Equal("""{"injected":"clipboard"}""", sent.Body);
    }

    [Fact]
    public async Task testPrewarmNeverThrows()
    {
        var reached = new TaskCompletionSource();
        var handler = new FakeHandler((_, _) =>
        {
            reached.TrySetResult();
            throw new HttpRequestException(HttpRequestError.ConnectionError, "offline");
        });
        using var api = client(handler);
        api.Prewarm();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Head, sent.Method);
        Assert.Equal("/health", sent.Uri.AbsolutePath);
        Assert.Null(sent.Authorization);
    }
}

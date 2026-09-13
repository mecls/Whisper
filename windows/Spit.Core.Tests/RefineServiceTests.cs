namespace Spit.Core.Tests;

/// Port of mac/VoiceTests/RefineServiceTests.swift.
public sealed class RefineServiceTests
{
    private static Dictation dictation(string raw) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, null) { Raw = raw, AudioMs = 3000, AsrMs = 900, Language = "pt" };

    private static RefineService service(StubApi api, Outbox outbox) =>
        new(api, outbox, new InMemoryLocalSettings(), asrModel: "whisper.cpp/test", clientVersion: "0.0.0-test");

    private static Result<RefineResponse> response(string cleaned, string raw, string? model, int llmMs, FallbackReason? fallbackReason) =>
        Result<RefineResponse>.Success(new RefineResponse("x", cleaned, raw, model, llmMs, fallbackReason));

    [Fact]
    public async Task testCleanedResponse()
    {
        var api = new StubApi { RefineResult = response("Olá.", "olá", "gemma4", 500, null) };
        var s = service(api, new Outbox());
        var r = await s.RefineAsync(dictation("olá"), "clean");
        Assert.Equal<RefineResult>(new RefineResult.Cleaned("Olá."), r);
    }

    // Rules 11-14: a transcript the gate clears never reaches the network, but is still logged — the skip
    // rate is the only signal that says whether the gate's thresholds are right.
    [Fact]
    public async Task testGateSkipsCleanupEntirelyAndStillLogsIt()
    {
        var api = new StubApi { RefineResult = response("SHOULD NOT BE USED", "", "gemma4", 500, null) };
        var s = service(api, new Outbox());
        var r = await s.RefineAsync(dictation("Bom dia, a reunião está confirmada."), "clean");
        Assert.Equal<RefineResult>(new RefineResult.Skipped(), r);
        var posted = Assert.Single(api.Posted);   // a skipped dictation is still recorded
        Assert.Equal(CleanupEngine.Skipped, posted.LlmModel);
        Assert.Equal(Injected.Raw, posted.Injected);
        Assert.Null(posted.Cleaned);
    }

    // A transcript that needs work must still take the server path, or the gate would be silently
    // swallowing the cleanup the user is paying for.
    [Fact]
    public async Task testDirtyTranscriptStillGoesToTheServer()
    {
        var api = new StubApi { RefineResult = response("Olá.", "hã olá", "gemma4", 500, null) };
        var s = service(api, new Outbox());
        var r = await s.RefineAsync(dictation("Hã, olá."), "clean");
        Assert.Equal<RefineResult>(new RefineResult.Cleaned("Olá."), r);
    }

    // Rule 15/18: the measurement has to reach the server, on the patch for a dictation the server already
    // stored and on the queued entry for one it has not.
    [Fact]
    public async Task testTotalMsReachesThePatch()
    {
        var api = new StubApi { RefineResult = response("Olá.", "olá", "gemma4", 1, null) };
        var s = service(api, new Outbox());
        var d = dictation("olá");
        _ = await s.RefineAsync(d, "clean");
        await s.ReportInjectedAsync(d.ClientId, Injected.Cleaned, totalMs: 742);
        Assert.Equal([742], api.PatchedTotalMs);
    }

    [Fact]
    public async Task testServerSideFallbackPastesServerRaw()
    {
        var api = new StubApi { RefineResult = response("olá", "olá", null, 0, FallbackReason.LlmBusy) };
        var s = service(api, new Outbox());
        var r = await s.RefineAsync(dictation("olá"), "clean");
        Assert.Equal<RefineResult>(new RefineResult.RawFallback(FallbackReason.LlmBusy), r);
    }

    // Mac C2: `Offline` models the unreachable server, and ReportInjected targets the same dictation's
    // clientId (the one refine actually saw), matching how the pending map is keyed.
    [Fact]
    public async Task testOfflineQueuesToOutboxAndReplaysLater()
    {
        var api = new StubApi();
        var outbox = new Outbox();
        var s = service(api, outbox);
        api.Offline = true;
        var d = dictation("olá");
        var r1 = await s.RefineAsync(d, "clean");
        Assert.Equal<RefineResult>(new RefineResult.RawFallback(FallbackReason.Offline), r1);
        await s.ReportInjectedAsync(d.ClientId, Injected.Raw, totalMs: null);   // offline: cannot patch
        Assert.Equal(1, outbox.Count);
        api.Offline = false;
        api.RefineResult = response("Olá.", "olá", "gemma4", 1, null);
        _ = await s.RefineAsync(dictation("olá"), "clean");
        var posted = Assert.Single(api.Posted);
        Assert.Equal(0, outbox.Count);
        // G2: the replayed entry is the offline dictation's — it must carry the offline reason it actually
        // failed with, not null and not some other value.
        Assert.Equal(FallbackReason.Offline, posted.FallbackReason);
    }

    [Fact]
    public async Task testLiteralSkipsNetworkButStillLogs()
    {
        var api = new StubApi();
        var s = service(api, new Outbox());
        var r = await s.RefineAsync(dictation("olá"), "literal");
        Assert.Equal<RefineResult>(new RefineResult.Literal(), r);
        var posted = Assert.Single(api.Posted);
        // G2: literal mode is a user choice, not a fallback — the posted row carries no fallback reason,
        // and the injected kind it was actually logged with.
        Assert.Null(posted.FallbackReason);
        Assert.Equal(Injected.Raw, posted.Injected);
    }

    [Fact]
    public async Task testUnauthorizedMapsToUnauthorized()
    {
        var api = new StubApi { RefineResult = Result<RefineResponse>.Failure(ApiException.Unauthorized()) };
        var s = service(api, new Outbox());
        var r = await s.RefineAsync(dictation("olá"), "clean");
        Assert.Equal<RefineResult>(new RefineResult.RawFallback(FallbackReason.Unauthorized), r);
    }
}

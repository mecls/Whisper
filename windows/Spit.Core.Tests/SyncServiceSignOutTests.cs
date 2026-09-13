namespace Spit.Core.Tests;

/// A `/v1/me` sent with the previous credential must not decide the state of the new one (third and fourth reviews).
public sealed class SyncServiceSignOutTests
{
    [Fact]
    public async Task ASyncFinishingAfterSignOutDoesNotRestoreTheName()
    {
        var api = new QueuedMeApi();
        var sync = new SyncService(api, new Settings());

        var inFlight = sync.SyncAsync();
        await api.Arrived(0);
        sync.SignOut();
        api.Reply(0, Me("Ana"));
        await inFlight;

        Assert.Null(sync.UserName);
        Assert.True(sync.Unauthorized);
    }

    [Fact]
    public async Task ASyncStartedAfterSignOutStillApplies()
    {
        var api = new QueuedMeApi();
        var sync = new SyncService(api, new Settings());
        sync.SignOut();

        var next = sync.SyncAsync();
        await api.Arrived(0);
        api.Reply(0, Me("Ana"));
        await next;

        Assert.Equal("Ana", sync.UserName);
        Assert.False(sync.Unauthorized);
    }

    [Fact]
    public async Task ALate401FromTheOldTokenDoesNotMarkANewTokenInvalid()
    {
        var api = new QueuedMeApi();
        var sync = new SyncService(api, new Settings());

        var stale = sync.SyncAsync();
        await api.Arrived(0);
        sync.TokenChanged();
        var fresh = sync.SyncAsync();
        await api.Arrived(1);
        api.Reply(1, Me("Ana"));
        await fresh;
        api.Fail(0, ApiException.Unauthorized());
        await stale;

        Assert.Equal("Ana", sync.UserName);
        Assert.False(sync.Unauthorized);
    }

    private static MeResponse Me(string name) =>
        new(new MeUser("u1", name), new ServerSettings("clean", "auto", "fn"), [], new MeServer("1", "m", 1, []));

    private sealed class Settings : ILocalSettings
    {
        public string Mode { get; set; } = "clean";
        public string Language { get; set; } = "auto";
        public string Hotkey { get; set; } = "rightCtrl";
    }

    /// Each `MeAsync` call gets its own reply, released by the test in any order.
    private sealed class QueuedMeApi : IVoiceApiClient
    {
        private const int Slots = 4;
        private readonly TaskCompletionSource[] arrived = Enumerable.Range(0, Slots).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private readonly TaskCompletionSource<MeResponse>[] replies = Enumerable.Range(0, Slots).Select(_ => new TaskCompletionSource<MeResponse>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private int calls;

        public Task Arrived(int call) => arrived[call].Task;
        public void Reply(int call, MeResponse me) => replies[call].SetResult(me);
        public void Fail(int call, Exception e) => replies[call].SetException(e);

        public Task<MeResponse> MeAsync()
        {
            var call = Interlocked.Increment(ref calls) - 1;
            arrived[call].TrySetResult();
            return replies[call].Task;
        }

        public Task<RefineResponse> RefineAsync(RefineRequest body, int budgetMs) => throw new NotSupportedException();
        public Task PostDictationAsync(DictationRequest body) => throw new NotSupportedException();
        public Task PatchInjectedAsync(Guid clientId, Injected injected, int? totalMs) => throw new NotSupportedException();
        public void Prewarm() { }
        public Task<Insights> InsightsAsync(string tz, int weeks) => throw new NotSupportedException();
        public Task PutSettingsAsync(ServerSettings settings) => Task.CompletedTask;
        public Task<IReadOnlyList<DictionaryEntry>> ListTermsAsync() => throw new NotSupportedException();
        public Task<DictionaryEntry> AddTermAsync(string term, string? replacement, bool teamWide) => throw new NotSupportedException();
        public Task DeleteTermAsync(string id) => throw new NotSupportedException();
    }
}

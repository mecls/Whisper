namespace Spit.Core.Tests;

/// A `/v1/me` that was already on its way when the user signed out must not sign them back in (third review).
public sealed class SyncServiceSignOutTests
{
    [Fact]
    public async Task ASyncFinishingAfterSignOutDoesNotRestoreTheName()
    {
        var api = new HeldMeApi();
        var sync = new SyncService(api, new Settings());

        var inFlight = sync.SyncAsync();
        await api.Requested.Task;
        sync.SignOut();
        api.Reply.SetResult(new MeResponse(new MeUser("u1", "Ana"), new ServerSettings("clean", "auto", "fn"), [], new MeServer("1", "m", 1, [])));
        await inFlight;

        Assert.Null(sync.UserName);
        Assert.True(sync.Unauthorized);
    }

    [Fact]
    public async Task ASyncStartedAfterSignOutStillApplies()
    {
        var api = new HeldMeApi();
        var sync = new SyncService(api, new Settings());
        sync.SignOut();

        var next = sync.SyncAsync();
        await api.Requested.Task;
        api.Reply.SetResult(new MeResponse(new MeUser("u1", "Ana"), new ServerSettings("clean", "auto", "fn"), [], new MeServer("1", "m", 1, [])));
        await next;

        Assert.Equal("Ana", sync.UserName);
        Assert.False(sync.Unauthorized);
    }

    private sealed class Settings : ILocalSettings
    {
        public string Mode { get; set; } = "clean";
        public string Language { get; set; } = "auto";
        public string Hotkey { get; set; } = "rightCtrl";
    }

    private sealed class HeldMeApi : IVoiceApiClient
    {
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<MeResponse> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MeResponse> MeAsync()
        {
            Requested.TrySetResult();
            return Reply.Task;
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

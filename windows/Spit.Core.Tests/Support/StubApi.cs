namespace Spit.Core.Tests;

/// Swift's `Result<Success, Error>`, as far as the stub needs it.
public sealed class Result<T>
{
    private readonly T? _value;
    private readonly Exception? _error;

    private Result(T? value, Exception? error)
    {
        _value = value;
        _error = error;
    }

    public static Result<T> Success(T value) => new(value, null);
    public static Result<T> Failure(Exception error) => new(default, error);

    public T Get() => _error is null ? _value! : throw _error;
}

/// Shared test double for `IVoiceApiClient`, port of mac/VoiceTests/StubAPI.swift. `Offline` forces every
/// call to throw `offline` regardless of the individual `*Result` properties — this is what lets a test
/// flip the whole stub from "server unreachable" to "server reachable" with one bool.
public sealed class StubApi : IVoiceApiClient
{
    public Result<RefineResponse> RefineResult { get; set; } = Result<RefineResponse>.Failure(ApiException.Offline());
    public Result<MeResponse> MeResult { get; set; } = Result<MeResponse>.Failure(ApiException.Offline());
    public List<DictationRequest> Posted { get; } = [];
    public List<(Guid ClientId, Injected Injected)> Patched { get; } = [];
    public List<int?> PatchedTotalMs { get; } = [];
    public int Prewarms { get; private set; }
    public Result<Insights> InsightsResult { get; set; } = Result<Insights>.Failure(ApiException.Offline());
    public List<(string Tz, int Weeks)> InsightsCalls { get; } = [];
    public List<ServerSettings> Puts { get; } = [];

    /// The Mac's `delay` (nanoseconds there): applied to refine and insights.
    public TimeSpan Delay { get; set; }

    /// Windows addition: when set, refine and insights wait for it — a deterministic stand-in for `Delay`.
    public Task? Gate { get; set; }

    public bool Offline { get; set; }

    // D3: in-memory dictionary for ListTerms/AddTerm/DeleteTerm.
    public List<DictionaryEntry> Terms { get; } = [];

    public async Task<RefineResponse> RefineAsync(RefineRequest body, int budgetMs)
    {
        if (Offline) throw ApiException.Offline();
        await WaitAsync();
        return RefineResult.Get();
    }

    public Task PostDictationAsync(DictationRequest body)
    {
        if (Offline) throw ApiException.Offline();
        Posted.Add(body);
        return Task.CompletedTask;
    }

    public Task PatchInjectedAsync(Guid clientId, Injected injected, int? totalMs)
    {
        if (Offline) throw ApiException.Offline();
        Patched.Add((clientId, injected));
        PatchedTotalMs.Add(totalMs);
        return Task.CompletedTask;
    }

    public void Prewarm() => Prewarms++;

    public async Task<Insights> InsightsAsync(string tz, int weeks)
    {
        if (Offline) throw ApiException.Offline();
        InsightsCalls.Add((tz, weeks));
        await WaitAsync();
        return InsightsResult.Get();
    }

    public Task<MeResponse> MeAsync()
    {
        if (Offline) throw ApiException.Offline();
        return Task.FromResult(MeResult.Get());
    }

    public Task PutSettingsAsync(ServerSettings settings)
    {
        if (Offline) throw ApiException.Offline();
        Puts.Add(settings);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DictionaryEntry>> ListTermsAsync()
    {
        if (Offline) throw ApiException.Offline();
        return Task.FromResult<IReadOnlyList<DictionaryEntry>>(Terms.ToArray());
    }

    public Task<DictionaryEntry> AddTermAsync(string term, string? replacement, bool teamWide)
    {
        if (Offline) throw ApiException.Offline();
        var entry = new DictionaryEntry(Guid.NewGuid().ToString(), term, replacement, null, teamWide, 0);
        Terms.Add(entry);
        return Task.FromResult(entry);
    }

    public Task DeleteTermAsync(string id)
    {
        if (Offline) throw ApiException.Offline();
        Terms.RemoveAll(t => t.Id == id);
        return Task.CompletedTask;
    }

    private async Task WaitAsync()
    {
        if (Gate is { } gate) await gate;
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
    }
}

namespace Spit.Core;

/// An unsent dictation report. Port of `OutboxEntry` in mac/Voice/Refine/Outbox.swift.
/// `Fallback` is nullable: literal-mode dictations carry no fallback reason at all (Mac G2).
public sealed record OutboxEntry(
    Guid ClientId,
    string Raw,
    Injected Injected,
    FallbackReason? Fallback,
    DateTimeOffset CreatedAt,
    string Mode,
    string LanguageSetting,
    string? LanguageDetected,
    FrontmostApp? App,
    int AudioMs,
    int AsrMs)
{
    // An offline dictation still has to contribute its measurement once connectivity returns, so the
    // timing rides the queued entry rather than being recomputed at replay time.
    public string? Cleaned { get; init; }
    public int? LlmMs { get; init; }
    public string? LlmModel { get; init; }
    public int? TotalMs { get; init; }
}

/// Memory only — no transcript ever touches disk (rule 25). Capped; oldest dropped first.
/// Port of mac/Voice/Refine/Outbox.swift; locked because Windows has no main actor to serialise it.
public sealed class Outbox
{
    public const int Cap = 200;

    private readonly List<OutboxEntry> _entries = [];
    private readonly Lock _lock = new();

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    public void Add(OutboxEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > Cap) _entries.RemoveRange(0, _entries.Count - Cap);
        }
    }

    public IReadOnlyList<OutboxEntry> Drain()
    {
        lock (_lock)
        {
            var all = _entries.ToArray();
            _entries.Clear();
            return all;
        }
    }

    public void Requeue(IEnumerable<OutboxEntry> entries)
    {
        foreach (var e in entries) Add(e);
    }
}

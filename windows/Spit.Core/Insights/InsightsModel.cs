namespace Spit.Core;

/// Drives the Insights view: cache first, then one refresh. Port of the non-UI half of
/// mac/Voice/Insights/InsightsModel.swift — `@Published` becomes a property plus an event.
///
/// There is deliberately no retry loop, no backoff timer and no background polling. A failed refresh
/// leaves the cached numbers exactly where they are and tries again the next time the window opens
/// (insights rule 23) — a dashboard that silently retries hammers the VPS while sitting open all day.
public sealed class InsightsModel
{
    /// 21 weeks plus the current one is what fits the card at its default width without scrolling.
    public const int Weeks = 21;

    private readonly IVoiceApiClient _api;
    private readonly InsightsCache _cache;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private Insights? _insights;
    private bool _isRefreshing;
    private ApiException? _failure;
    private bool _timeZoneUnknown;
    private Task? _inFlight;

    public InsightsModel(IVoiceApiClient api, InsightsCache cache, TimeProvider? timeProvider = null)
    {
        _api = api;
        _cache = cache;
        _time = timeProvider ?? TimeProvider.System;
        // Read before the first render, so the window opens with numbers already on it rather than
        // flashing a loading state it will leave 200 ms later.
        _insights = cache.Load();
        Presentation = InsightsPresentation.Make(_insights, isRefreshing: false, failure: null);
    }

    public InsightsPresentation Presentation { get; private set; }

    /// Raised after `Presentation` changes, on the thread that changed it (the caller's context for the
    /// start of a refresh, the awaited continuation for its end).
    public event EventHandler? PresentationChanged;

    /// The cache write started by the last successful refresh. Held rather than discarded so tests can
    /// await it: a write that outlives its test recreates the temp directory teardown just removed.
    public Task? PendingSave { get; private set; }

    /// The zone the user's calendar days are in, as IANA, or null when it has none (rule 43). Read at
    /// refresh time, not at construction, so a zone change re-buckets days on the next refresh (the app
    /// calls `TimeZoneInfo.ClearCachedData` when Windows reports a change).
    public string? TimeZoneIdentifier => IanaTimeZone.TryGetIana(_time.LocalTimeZone, out var iana) ? iana : null;

    /// Starts a refresh and returns it; the app does not await it. A second call while one is in flight
    /// returns the first — two would only race each other to write the cache.
    public Task Refresh()
    {
        string tz;
        TaskCompletionSource done;
        lock (_gate)
        {
            if (_inFlight is not null) return _inFlight;
            if (TimeZoneIdentifier is not { } resolved)
            {
                // Rule 43: no IANA name, no request — and never a UTC stand-in.
                _failure = null;
                _timeZoneUnknown = true;
                done = new TaskCompletionSource();
                done.SetResult();
                tz = "";
            }
            else
            {
                tz = resolved;
                _isRefreshing = true;
                _failure = null;
                _timeZoneUnknown = false;
                done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight = done.Task;
            }
        }
        Publish();
        if (tz.Length > 0) _ = RunAsync(tz, done);
        return done.Task;
    }

    private async Task RunAsync(string tz, TaskCompletionSource done)
    {
        try
        {
            var fresh = await _api.InsightsAsync(tz, Weeks);
            lock (_gate)
            {
                _insights = fresh;
                _failure = null;
            }
            var cache = _cache;
            // Off the calling thread: the window is on screen and a disk write has no business being in
            // front of it, however small.
            PendingSave = Task.Run(() => cache.Save(fresh));
        }
        catch (ApiException e)
        {
            lock (_gate) _failure = e;
        }
        catch (Exception)
        {
            lock (_gate) _failure = ApiException.ServerStatus(-1);
        }

        lock (_gate)
        {
            _isRefreshing = false;
            _inFlight = null;
        }
        Publish();
        done.SetResult();
    }

    private void Publish()
    {
        lock (_gate) Presentation = InsightsPresentation.Make(_insights, _isRefreshing, _failure, _timeZoneUnknown);
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }
}

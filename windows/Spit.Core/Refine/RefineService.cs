using System.Collections.Concurrent;
using System.Globalization;

namespace Spit.Core;

/// Budgeted refine with raw fallback; outbox replay after any successful call. Port of
/// mac/Voice/Refine/RefineService.swift. The Mac's `Preferences.language`, `Preferences.modelId` and
/// `VoiceAPI.version` globals are injected. Nothing here logs or persists transcript text (rule 25).
public sealed class RefineService : IRefiner
{
    private readonly IVoiceApiClient _api;
    private readonly Outbox _outbox;
    private readonly ILocalSettings _settings;
    private readonly string _clientVersion;

    /// Dictations whose refine never completed on our side, with the reason it failed. Reported through
    /// `POST /v1/dictations` (or the outbox) instead of a PATCH, since the server has no row to patch.
    private readonly ConcurrentDictionary<Guid, (Dictation Dictation, FallbackReason? Reason)> _pendingByClientId = new();

    public RefineService(IVoiceApiClient api, Outbox outbox, ILocalSettings settings, string asrModel, string clientVersion)
    {
        _api = api;
        _outbox = outbox;
        _settings = settings;
        AsrModel = asrModel;
        _clientVersion = clientVersion;
    }

    /// Sent as `asrModel`; settable because the user can switch models without relaunching.
    public string AsrModel { get; set; }

    public async Task<RefineResult> RefineAsync(Dictation dictation, string mode)
    {
        var d = dictation;
        if (d.Raw is not { } raw) return new RefineResult.RawFallback(FallbackReason.Server);
        var ctx = new RefineContext(d.App?.BundleId, d.App?.Name);
        var timing = new RefineTiming(d.AudioMs, d.AsrMs);
        var createdAt = d.StartedAt.ToUnixTimeMilliseconds();
        var languageSetting = _settings.Language;

        if (mode == "literal")
        {
            await LogOnlyAsync(d, Injected.Raw, fallback: null, mode).ConfigureAwait(false);
            return new RefineResult.Literal();
        }

        // Rules 11-14: a transcript that needs nothing skips the round trip entirely. Still logged, with
        // `llmModel` marking it, because the skip rate is the only signal that says whether the gate's
        // thresholds are right.
        if (SkipGate.ReasonToClean(raw, mode, d.Language, d.StreamedSegments) is null)
        {
            await LogOnlyAsync(d, Injected.Raw, fallback: null, mode, llmModel: CleanupEngine.Skipped).ConfigureAwait(false);
            return new RefineResult.Skipped();
        }

        var budget = Budget.Ms(new StringInfo(raw).LengthInTextElements);
        var body = new RefineRequest(d.WireId, raw, mode, languageSetting, d.Language, budget, ctx, timing,
            AsrModel, _clientVersion, createdAt);
        try
        {
            var r = await _api.RefineAsync(body, budget).ConfigureAwait(false);
            await ReplayOutboxAsync().ConfigureAwait(false);
            if (r.FallbackReason is { } serverReason) return new RefineResult.RawFallback(serverReason);
            return string.IsNullOrEmpty(r.Cleaned)
                ? new RefineResult.RawFallback(FallbackReason.GuardRejected)
                : new RefineResult.Cleaned(r.Cleaned);
        }
        catch (ApiException e)
        {
            var reason = e.Kind switch
            {
                ApiErrorKind.Timeout => FallbackReason.ClientTimeout,
                ApiErrorKind.Offline => FallbackReason.Offline,
                ApiErrorKind.Unauthorized => FallbackReason.Unauthorized,
                _ => FallbackReason.Server,   // server(status) and decoding
            };
            _pendingByClientId[d.ClientId] = (d, reason);
            return new RefineResult.RawFallback(reason);
        }
        catch (Exception)
        {
            _pendingByClientId[d.ClientId] = (d, FallbackReason.Server);
            return new RefineResult.RawFallback(FallbackReason.Server);
        }
    }

    public async Task ReportInjectedAsync(Guid clientId, Injected injected, int? totalMs)
    {
        if (_pendingByClientId.TryRemove(clientId, out var pending))
        {
            // The refine never completed on our side: log it (or queue it) instead of patching.
            await LogOnlyAsync(pending.Dictation, injected, pending.Reason, "clean", totalMs: totalMs).ConfigureAwait(false);
            return;
        }
        // Mac C3: the PATCH 404s until a POST has stored the row. This only runs after a successful
        // refine, so a failure here means that POST itself failed — do not retry.
        try
        {
            await _api.PatchInjectedAsync(clientId, injected, totalMs).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing to surface: the dictation was already pasted.
        }
    }

    private async Task LogOnlyAsync(Dictation d, Injected injected, FallbackReason? fallback, string mode,
        string? llmModel = null, int? totalMs = null)
    {
        // Mac G2: `fallback` passes through as-is — literal mode sends null, which must reach the server as
        // no fallback, not get coalesced into a false `offline`.
        var entry = new OutboxEntry(d.ClientId, d.Raw ?? "", injected, fallback, d.StartedAt, mode, _settings.Language,
            d.Language, d.App, d.AudioMs, d.AsrMs)
        {
            LlmModel = llmModel,
            TotalMs = totalMs,
        };
        try
        {
            await _api.PostDictationAsync(Request(entry)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _outbox.Add(entry);
        }
    }

    private async Task ReplayOutboxAsync()
    {
        var entries = _outbox.Drain();
        var failed = new List<OutboxEntry>();
        foreach (var e in entries)
        {
            try
            {
                await _api.PostDictationAsync(Request(e)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                failed.Add(e);
            }
        }
        _outbox.Requeue(failed);
    }

    private DictationRequest Request(OutboxEntry e) =>
        new(e.ClientId.ToString("D"), e.Raw, e.Injected, e.Fallback, e.Mode, e.LanguageSetting, e.LanguageDetected,
            new RefineContext(e.App?.BundleId, e.App?.Name), new RefineTiming(e.AudioMs, e.AsrMs),
            AsrModel, _clientVersion, e.CreatedAt.ToUnixTimeMilliseconds())
        {
            Cleaned = e.Cleaned,
            LlmMs = e.LlmMs,
            LlmModel = e.LlmModel,
            TotalMs = e.TotalMs,
        };
}

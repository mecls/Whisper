namespace Spit.Core;

/// `GET /v1/me` on launch and every 10 minutes. Server settings win; the client writes through PUT.
/// Port of mac/Voice/Refine/SyncService.swift, with the Windows hotkey rule (rule 46):
///
/// - the server's `hotkey` is the Mac's (`fn` | `rightOption` | `rightCommand`) and is never applied
///   here — there is no hotkey callback and `ILocalSettings.Hotkey` is never written;
/// - a PUT sends `hotkey` and `llmModel` exactly as last received (the Mac's G3 merge), so changing Mode
///   on the PC cannot reset the Mac's key to `fn`;
/// - with no successful `/v1/me` this process lifetime there is nothing to echo, so no PUT is sent at
///   all. The next successful sync's server values win, as on the Mac when its PUT fails.
public sealed class SyncService : IDisposable
{
    /// `GET /v1/me` period (rule 22).
    public const int IntervalSeconds = 600;

    private readonly IVoiceApiClient _api;
    private readonly ILocalSettings _settings;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private ITimer? _timer;

    /// The last settings object received from `/v1/me` (or successfully sent back). Null until the first
    /// successful sync, which is what gates the PUT.
    private ServerSettings? _last;

    /// Bumped by `SignOut`, so a `/v1/me` already in flight when the user signed out cannot put their name back.
    private int _signOuts;

    public SyncService(IVoiceApiClient api, ILocalSettings settings, TimeProvider? timeProvider = null)
    {
        _api = api;
        _settings = settings;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string? UserName { get; private set; }
    public bool Unauthorized { get; private set; }

    /// Raised when `UserName` or `Unauthorized` may have changed. On the thread that finished the sync.
    public event EventHandler? Changed;

    /// Receives the dictionary (`replacement ?? term`) on every successful sync — the Mac writes
    /// `DictionaryCache.shared`; the app persists it to `dictionary.json` (terms only, rule 25).
    public Action<IReadOnlyList<string>>? OnDictionaryChange { get; set; }

    public void Start()
    {
        _ = SyncAsync();
        var period = TimeSpan.FromSeconds(IntervalSeconds);
        _timer = _time.CreateTimer(_ => _ = SyncAsync(), null, period, period);
    }

    public async Task SyncAsync()
    {
        int signOuts;
        lock (_gate) signOuts = _signOuts;
        try
        {
            var me = await _api.MeAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (signOuts != _signOuts) return;
                UserName = me.User.Name;
                Unauthorized = false;
                _last = me.Settings;
            }
            _settings.Mode = me.Settings.Mode;
            _settings.Language = me.Settings.Language;
            // Rule 46: me.Settings.Hotkey is deliberately not applied.
            OnDictionaryChange?.Invoke(me.Dictionary.Select(t => t.Replacement ?? t.Term).ToArray());
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (ApiException e) when (e.Kind == ApiErrorKind.Unauthorized)
        {
            lock (_gate)
            {
                if (signOuts != _signOuts) return;
                Unauthorized = true;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // Offline or server trouble: keep the cached settings; the next tick tries again.
        }
    }

    /// A user-initiated Mode or Language change. The local write always happens; the PUT only when a
    /// `/v1/me` has succeeded this launch and something actually changed.
    public async Task PushAsync(string? mode = null, string? language = null)
    {
        if (mode is not null) _settings.Mode = mode;
        if (language is not null) _settings.Language = language;

        ServerSettings merged;
        lock (_gate)
        {
            // Rule 46: nothing received, nothing to echo — never invent a hotkey or clear an llmModel.
            if (_last is null) return;
            // G3: merge onto the last server settings so fields not being changed — `hotkey`, `llmModel`
            // — go back exactly as received.
            merged = _last with { Mode = mode ?? _last.Mode, Language = language ?? _last.Language };
            // No-op guard: kills the echo when a sync just wrote this same value into the settings UI.
            if (merged == _last) return;
        }

        try
        {
            await _api.PutSettingsAsync(merged).ConfigureAwait(false);
            lock (_gate) _last = merged;
        }
        catch (Exception)
        {
            // The next successful sync's server values win.
        }
    }

    /// The Server page's Sign out. The page deletes the token itself (only Save and Sign out write the
    /// credential, rule 44); this only drops the signed-in state. The timer keeps running; the next sync
    /// will 401 and stay unauthorized until a new token is saved.
    public void SignOut()
    {
        lock (_gate)
        {
            _signOuts++;
            Unauthorized = true;
            UserName = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _timer?.Dispose();
}

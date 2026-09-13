using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Spit.Core;

namespace Spit.App;

/// The one seam between the UI and everything else. The Coordinator sets the state properties and
/// assigns the intent delegates; every view reads the properties, listens to `PropertyChanged`, and
/// calls a `Request…` method — never a service directly (the Dictionary page's API calls excepted, as on
/// the Mac). All mutations are expected on the UI thread.
///
/// Intents are nullable delegates returning `Task`, one style throughout: an unassigned intent is a
/// no-op, and an intent that throws is logged (type only, never a message that could quote text) rather
/// than crashing the dispatcher. Settings that are plain toggles are written to `Store` here before the
/// intent runs, so a view needs nothing wired to change them and the Coordinator only reacts.
public sealed class AppModel : INotifyPropertyChanged
{
    private readonly Dispatcher dispatcher;

    private Settings settings;
    private HUDState hud = new HUDState.Hidden();
    private IReadOnlyList<float> levels = [];
    private bool isLatched;
    private bool paused;
    private string? lastText;
    private string? liveText;
    private string modelStatus = Strings.ModelNotDownloaded;
    private double? modelDownloadProgress;
    private string statusLine;
    private bool unauthorized;
    private string? userName;
    private bool hasToken;
    private bool tokenChecking;
    private MicrophoneState microphone = MicrophoneState.Unknown;
    private bool hotkeySeen;
    private bool modelDownloaded;
    private string activeModelLabel;

    public AppModel(SettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
        dispatcher = Dispatcher.CurrentDispatcher;
        settings = store.Current;
        statusLine = Strings.IdleFor(HotkeyOf(settings).Label());
        activeModelLabel = LabelFor(settings.ModelFile);
        store.Changed += OnStoreChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsStore Store { get; }

    // MARK: - Settings, mirrored from the store

    /// `SettingsStore.Current`, refreshed on the UI thread whenever the store changes.
    public Settings Settings
    {
        get => settings;
        private set
        {
            var idleBefore = Strings.IdleFor(HotkeyOf(settings).Label());
            if (!Set(ref settings, value)) return;
            Raise(nameof(Hotkey));
            // The idle line names the key; follow a key change unless the Coordinator shows something else.
            if (statusLine == idleBefore) StatusLine = Strings.IdleFor(HotkeyOf(value).Label());
        }
    }

    public HotkeyChoice Hotkey => HotkeyOf(settings);

    // MARK: - State the Coordinator sets

    public HUDState Hud
    {
        get => hud;
        set
        {
            if (Set(ref hud, value)) Raise(nameof(TrayIconState));
        }
    }

    /// The latest ≤ 20 RMS levels, 0…1, oldest first.
    public IReadOnlyList<float> Levels { get => levels; set => Set(ref levels, value ?? []); }

    public bool IsLatched
    {
        get => isLatched;
        set
        {
            if (Set(ref isLatched, value)) Raise(nameof(TrayIconState));
        }
    }

    public bool Paused { get => paused; set => Set(ref paused, value); }

    /// Held only so "Copy last dictation" can be enabled; no view displays it.
    public string? LastText { get => lastText; set => Set(ref lastText, value); }

    /// Streaming transcript for the bar, already filtered by `showTextInHUD` and live transcription.
    public string? LiveText { get => liveText; set => Set(ref liveText, value); }

    public string ModelStatus { get => modelStatus; set => Set(ref modelStatus, value ?? ""); }

    /// 0…1 while a model download runs; null otherwise.
    public double? ModelDownloadProgress { get => modelDownloadProgress; set => Set(ref modelDownloadProgress, value); }

    /// The tray's first line when not paused ("Ready — hold Right Ctrl to dictate").
    public string StatusLine { get => statusLine; set => Set(ref statusLine, value ?? ""); }

    public bool Unauthorized
    {
        get => unauthorized;
        set
        {
            if (Set(ref unauthorized, value)) Raise(nameof(TrayIconState));
        }
    }

    public string? UserName { get => userName; set => Set(ref userName, value); }

    /// Whether Credential Manager holds a token for the server URL — separates "Not connected" from
    /// "Token invalid".
    public bool HasToken { get => hasToken; set => Set(ref hasToken, value); }

    /// True while `/v1/me` runs (the Set-up Token row's "Checking…"). Also set by `RequestSaveToken` and
    /// `RequestTestConnection` for as long as their intent runs.
    public bool TokenChecking { get => tokenChecking; set => Set(ref tokenChecking, value); }

    public MicrophoneState Microphone { get => microphone; set => Set(ref microphone, value); }

    /// The hook has seen the dictation key since the Set-up window opened.
    public bool HotkeySeen { get => hotkeySeen; set => Set(ref hotkeySeen, value); }

    /// Whether the model picked in settings (`Settings.ModelFile`) is on disk and verified.
    public bool ModelDownloaded { get => modelDownloaded; set => Set(ref modelDownloaded, value); }

    /// The label of the model actually loaded, which can differ from the picker's selection (Mac H3).
    public string ActiveModelLabel { get => activeModelLabel; set => Set(ref activeModelLabel, value ?? ""); }

    /// Derived: the tray icon's look, ranked like the Mac's `menuIcon`.
    public MenuIconState TrayIconState => MenuIcon.For(unauthorized, isLatched, hud);

    // MARK: - Intents the Coordinator assigns

    public Func<Task>? MicTapped { get; set; }
    public Func<Task>? CopyLastDictation { get; set; }
    public Func<Task>? TogglePause { get; set; }
    public Func<bool, Task>? ShowBarChanged { get; set; }
    public Func<bool, Task>? LiveTranscriptionChanged { get; set; }
    public Func<string, Task>? ModeChanged { get; set; }
    public Func<string, Task>? LanguageChanged { get; set; }
    public Func<HotkeyChoice, Task>? HotkeyChanged { get; set; }
    public Func<Task>? OpenInsights { get; set; }
    public Func<SettingsSection, Task>? OpenSettings { get; set; }
    public Func<Task>? OpenSetup { get; set; }
    public Func<Task>? Quit { get; set; }
    public Func<string, Task>? DownloadModel { get; set; }

    /// Throws on failure; the Model page shows `Strings.ModelDeleteError(message)`.
    public Func<string, Task>? DeleteModel { get; set; }

    public Func<Task>? ReloadModel { get; set; }

    /// After the picker wrote `modelFile`: the Coordinator reloads when that file is already downloaded
    /// (Mac H2) and recomputes `ModelDownloaded`.
    public Func<string, Task>? ModelFileChanged { get; set; }

    /// Saves the credential and syncs. Throws when the credential can't be written.
    public Func<string, Task>? SaveToken { get; set; }

    public Func<Task>? SignOut { get; set; }
    public Func<Task>? TestConnection { get; set; }
    public Func<Task>? OpenMicrophoneSettings { get; set; }
    public Func<Task>? RefreshInsights { get; set; }

    /// Returns null on success or the failure's message; the General page reverts the toggle and shows
    /// `Strings.LaunchAtLoginError(message)`.
    public Func<bool, Task<string?>>? SetLaunchAtLogin { get; set; }

    /// After the Dictionary page added or deleted a term: the Coordinator syncs so the next dictation's
    /// prompt has it (Mac `sync.sync()`).
    public Func<Task>? DictionaryChanged { get; set; }

    // MARK: - What the views call

    public Task RequestMicTap() => Run(MicTapped, "mic tap");

    public Task RequestCopyLastDictation() => Run(CopyLastDictation, "copy last dictation");

    public Task RequestTogglePause() => Run(TogglePause, "toggle pause");

    public Task RequestShowBar(bool on)
    {
        Store.Update(s => s with { ShowBar = on });
        return Run(ShowBarChanged, on, "show bar");
    }

    public Task RequestLiveTranscription(bool on)
    {
        Store.Update(s => s with { LiveTranscription = on });
        return Run(LiveTranscriptionChanged, on, "live transcription");
    }

    public void SetSounds(bool on) => Store.Update(s => s with { Sounds = on });

    public void SetShowTextInHUD(bool on) => Store.Update(s => s with { ShowTextInHUD = on });

    /// The server URL takes effect on relaunch (`Strings.ServerURLChangeNote`), so this only stores it.
    public void SetServerURL(string url) => Store.Update(s => s with { ServerURL = url.Trim() });

    public Task RequestMode(string mode)
    {
        Store.Update(s => s with { Mode = mode });
        return Run(ModeChanged, mode, "mode");
    }

    public Task RequestLanguage(string language)
    {
        Store.Update(s => s with { Language = language });
        return Run(LanguageChanged, language, "language");
    }

    public Task RequestHotkey(HotkeyChoice choice)
    {
        Store.Update(s => s with { Hotkey = choice.RawValue() });
        return Run(HotkeyChanged, choice, "hotkey");
    }

    public Task RequestOpenInsights() => Run(OpenInsights, "open insights");

    public Task RequestOpenSettings(SettingsSection section) => Run(OpenSettings, section, "open settings");

    public Task RequestOpenSetup() => Run(OpenSetup, "open set-up");

    public Task RequestQuit() => Run(Quit, "quit");

    public Task RequestDownloadModel(string file) => Run(DownloadModel, file, "download model");

    /// Null on success, otherwise the error line to show.
    public async Task<string?> RequestDeleteModel(string file)
    {
        if (DeleteModel is null) return null;
        try
        {
            await DeleteModel(file);
            return null;
        }
        catch (Exception e)
        {
            Log.Failure("ui", "delete model", e);
            return Strings.ModelDeleteError(e.Message);
        }
    }

    public Task RequestReloadModel() => Run(ReloadModel, "reload model");

    public Task RequestModelFile(string file)
    {
        Store.Update(s => s with { ModelFile = file });
        return Run(ModelFileChanged, file, "model file");
    }

    /// Null on success, otherwise the error line to show.
    public async Task<string?> RequestSaveToken(string token)
    {
        if (SaveToken is null) return null;
        TokenChecking = true;
        try
        {
            await SaveToken(token);
            HasToken = true;
            return null;
        }
        catch (Exception e)
        {
            Log.Failure("ui", "save token", e);
            return Strings.SaveTokenError(e.Message);
        }
        finally
        {
            TokenChecking = false;
        }
    }

    public async Task RequestSignOut()
    {
        await Run(SignOut, "sign out");
        HasToken = false;
    }

    public async Task RequestTestConnection()
    {
        TokenChecking = true;
        try
        {
            await Run(TestConnection, "test connection");
        }
        finally
        {
            TokenChecking = false;
        }
    }

    public Task RequestOpenMicrophoneSettings() => Run(OpenMicrophoneSettings, "open microphone settings");

    public Task RequestRefreshInsights() => Run(RefreshInsights, "refresh insights");

    /// Null on success, otherwise the failure's message (unformatted).
    public async Task<string?> RequestLaunchAtLogin(bool on)
    {
        if (SetLaunchAtLogin is null) return null;
        try
        {
            return await SetLaunchAtLogin(on);
        }
        catch (Exception e)
        {
            Log.Failure("ui", "launch at login", e);
            return e.Message;
        }
    }

    public Task RequestDictionaryChanged() => Run(DictionaryChanged, "dictionary changed");

    /// The Set-up window's Done.
    public void CompleteOnboarding() => Store.Update(s => s with { Onboarded = true });

    // MARK: - Helpers

    public static string LabelFor(string modelFile) => ModelCatalog.Default.Find(modelFile)?.Label ?? modelFile;

    private static HotkeyChoice HotkeyOf(Settings s) => HotkeyChoiceExtensions.FromRawValue(s.Hotkey) ?? HotkeyChoice.RightCtrl;

    private void OnStoreChanged(object? sender, Settings next)
    {
        if (dispatcher.CheckAccess()) Settings = next;
        else dispatcher.BeginInvoke(() => Settings = Store.Current);
    }

    private static async Task Run(Func<Task>? intent, string what)
    {
        if (intent is null) return;
        try
        {
            await intent();
        }
        catch (Exception e)
        {
            Log.Failure("ui", what, e);
        }
    }

    private static async Task Run<T>(Func<T, Task>? intent, T argument, string what)
    {
        if (intent is null) return;
        try
        {
            await intent(argument);
        }
        catch (Exception e)
        {
            Log.Failure("ui", what, e);
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

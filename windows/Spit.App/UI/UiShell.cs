using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Spit.Core;

namespace Spit.App;

/// Creates and owns every piece of UI — tray, bar, main window, set-up window — and nothing else. No
/// business logic: the Coordinator drives all of it through `AppModel`. Intents the shell can satisfy by
/// itself (navigation, refreshing Insights, opening Windows' microphone settings, the Run key, quitting)
/// get a default here when the Coordinator hasn't assigned one.
public sealed class UiShell : IDisposable
{
    public const string MicrophoneSettingsUri = "ms-settings:privacy-microphone";

    private AppModel? model;
    private TrayIcon? tray;
    private BarWindow? bar;
    private MainWindow? main;
    private SetupWindow? setup;
    private InsightsPage? insightsPage;
    private SettingsPage? settingsPage;
    private bool disposed;

    public BarWindow? Bar => bar;

    public MainWindow? Main => main;

    /// Call once, on the UI thread, after `Application` exists. `settings` must be the store `model` was
    /// built on. `launchAtLogin` defaults to the real `HKCU\…\Run\Spit` value.
    public void Start(AppModel model, SettingsStore settings, Func<IVoiceApiClient> api, InsightsModel insights, LaunchAtLogin? launchAtLogin = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(insights);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (this.model is not null) throw new InvalidOperationException("The UI has already been started.");
        if (!ReferenceEquals(model.Store, settings)) throw new ArgumentException("The model must be built on this settings store.", nameof(settings));
        this.model = model;
        var launch = launchAtLogin ?? new LaunchAtLogin();

        model.OpenInsights ??= () => Done(() => ShowMain(MainSection.Insights));
        model.OpenSettings ??= section => Done(() => ShowMain(MainSection.Settings, section));
        model.OpenSetup ??= () => Done(ShowSetup);
        model.RefreshInsights ??= insights.Refresh;
        model.OpenMicrophoneSettings ??= () => Done(OpenMicrophoneSettings);
        model.SetLaunchAtLogin ??= on => Task.Run(() =>
        {
            try
            {
                launch.SetEnabled(on);
                return (string?)null;
            }
            catch (InvalidOperationException e)
            {
                return e.Message;
            }
        });
        model.Quit ??= () => Done(() => Application.Current?.Shutdown());

        insightsPage = new InsightsPage(model, insights);
        settingsPage = new SettingsPage(model, api, launch);
        main = new MainWindow(insightsPage, settingsPage);
        setup = new SetupWindow(model);
        bar = new BarWindow(model);
        tray = new TrayIcon(model, () => ShowMain());

        tray.Show();
        // The bar follows Show bar from here on by itself, whichever of tray, page or file changed it.
        bar.ApplyVisibility();

        // Like the Mac's AppDelegate: the set-up window opens by itself only until it has been completed once.
        if (!settings.Current.Onboarded) ShowSetup();
        // CI's install check requires this line: a UI that failed to build leaves a running process with no tray.
        Log.Info("ui", UiReadyMessage);
    }

    public const string UiReadyMessage = "tray, bar and windows ready";

    /// Shows the main window where it was (the tray's left-click, a second launch).
    public void ShowMain() => main?.ShowSection();

    public void ShowMain(MainSection section, SettingsSection? settingsTab = null) => main?.ShowSection(section, settingsTab);

    public void ShowSetup() => setup?.ShowAndActivate();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        tray?.Dispose();
        bar?.CloseForShutdown();
        setup?.CloseForShutdown();
        main?.CloseForShutdown();
        insightsPage?.Dispose();
        settingsPage?.Dispose();
        ThemePalette.Detach();
    }

    private static void OpenMicrophoneSettings()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(MicrophoneSettingsUri) { UseShellExecute = true });
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Failure("ui", "open microphone settings", e);
        }
    }

    private static Task Done(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}

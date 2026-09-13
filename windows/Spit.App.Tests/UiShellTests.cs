using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Spit.Core;

namespace Spit.App.Tests;

/// Builds the whole UI the way `App.OnStartup` does, shows every window and tab, and walks every state the
/// Coordinator can put the model in — twice, because a state coming back is its own case. XAML resource lookups,
/// styles and third-party controls are only exercised at runtime, so compiling on the Mac proves nothing about
/// them: a key defined twice in `Theme.xaml` once threw inside `UiShell.Start`, and the tray threw
/// `ObjectDisposedException` the first time an icon state came back — both invisible to three code reviews.
[Collection(ClipboardCollection.Name)]
public sealed class UiShellTests
{
    [WindowsFact]
    public void TheWholeShellBuildsAndEveryWindowAndStateRendersWithoutAnError()
    {
        Exception? failure = null;
        var root = Directory.CreateTempSubdirectory("spit-ui-test").FullName;
        var thread = new Thread(() =>
        {
            try
            {
                var paths = new AppPaths(root);
                var settings = new SettingsStore(paths);
                var model = new AppModel(settings);
                // Port 9 (discard) on loopback: Insights and the dictionary fail fast and show their offline states.
                using var api = new HttpVoiceApiClient(new Uri("http://127.0.0.1:9/"), () => null, "test");
                var insights = new InsightsModel(api, new InsightsCache(Path.Combine(root, InsightsCache.FileName)));
                var shell = new UiShell();
                try
                {
                    shell.Start(model, settings, () => api, insights);
                    Pump();

                    shell.ShowMain(MainSection.Insights);
                    Pump();
                    foreach (var tab in Enum.GetValues<SettingsSection>())
                    {
                        shell.ShowMain(MainSection.Settings, tab);
                        Pump();
                    }
                    shell.ShowSetup();
                    Pump();

                    for (var round = 0; round < 2; round++)
                    {
                        WalkBarAndTray(model);
                        WalkServerAndModelState(model);
                    }
                }
                finally
                {
                    shell.Dispose();
                    Pump();
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "building the UI hung");
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    /// Every HUD state in the order a dictation produces them, plus the latched and unauthorized icons.
    private static void WalkBarAndTray(AppModel model)
    {
        HUDState[] states =
        [
            new HUDState.Hidden(),
            new HUDState.Listening(),
            new HUDState.Transcribing(null),
            new HUDState.Transcribing(0.5),
            new HUDState.Cleaning(),
            new HUDState.Done("Hello there.", Strings.ViaCleaned),
            new HUDState.Done(null, null),
            new HUDState.Message(Strings.NothingHeard),
            new HUDState.Message(Strings.MicrophoneBlocked),
            new HUDState.ModelLoading(0.3),
            new HUDState.Hidden(),
        ];
        foreach (var state in states)
        {
            model.Hud = state;
            if (state is HUDState.Listening)
            {
                model.Levels = [0.05f, 0.2f, 0.6f, 0.9f, 0.3f];
                model.LiveText = "live words so far";
            }
            Pump();
        }

        model.Hud = new HUDState.Listening();
        model.IsLatched = true;
        Pump();
        model.IsLatched = false;
        model.Hud = new HUDState.Hidden();
        Pump();

        model.Unauthorized = true;
        Pump();
        model.Unauthorized = false;
        model.Paused = true;
        Pump();
        model.Paused = false;
        model.LastText = "the last dictation";
        model.StatusLine = Strings.TokenInvalid;
        Pump();
        model.StatusLine = Strings.Idle;
        Pump();
    }

    private static void WalkServerAndModelState(AppModel model)
    {
        model.TokenChecking = true;
        Pump();
        model.TokenChecking = false;
        model.HasToken = true;
        model.UserName = "Ana";
        Pump();
        model.UserName = null;
        model.HasToken = false;
        Pump();

        foreach (var microphone in Enum.GetValues<MicrophoneState>())
        {
            model.Microphone = microphone;
            Pump();
        }
        model.HotkeySeen = true;
        Pump();

        model.ModelDownloaded = false;
        model.ModelDownloadProgress = 0.4;
        model.ModelStatus = Strings.ModelDownloading(40);
        Pump();
        model.ModelDownloadProgress = null;
        model.ModelDownloaded = true;
        model.ModelStatus = Strings.ModelReady;
        model.ActiveModelLabel = Strings.ModelLabelTurbo;
        Pump();
    }

    /// Runs layout, loading and rendering work queued so far — where styles and dynamic resources are applied.
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}

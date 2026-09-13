using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Spit.Core;

namespace Spit.App.Tests;

/// Builds the whole UI the way `App.OnStartup` does and shows every window. XAML resource lookups are resolved
/// at runtime, so compiling on the Mac proves nothing about them: a key defined twice in `Theme.xaml` once threw
/// inside `UiShell.Start` and would have left Spit running with no tray, bar or windows (third review).
[Collection(ClipboardCollection.Name)]
public sealed class UiShellTests
{
    [WindowsFact]
    public void TheWholeShellBuildsAndEveryWindowShowsWithoutAXamlOrResourceError()
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
                // Port 9 (discard) on loopback: Insights and sync fail fast and show their offline states.
                using var api = new HttpVoiceApiClient(new Uri("http://127.0.0.1:9/"), () => null, "test");
                var insights = new InsightsModel(api, new InsightsCache(Path.Combine(root, InsightsCache.FileName)));
                var shell = new UiShell();
                try
                {
                    shell.Start(model, settings, () => api, insights);
                    shell.ShowMain(MainSection.Insights, null);
                    Pump();
                    shell.ShowMain(MainSection.Settings, null);
                    Pump();
                    shell.ShowSetup();
                    model.Hud = new HUDState.Listening();
                    model.Levels = [0.1f, 0.4f, 0.8f];
                    Pump();
                    model.Hud = new HUDState.Message(Strings.NothingHeard);
                    Pump();
                }
                finally
                {
                    shell.Dispose();
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

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "building the UI hung");
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
    }

    /// Runs layout, loading and rendering work queued so far — where styles and dynamic resources are applied.
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}

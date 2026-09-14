using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Spit.App;

/// Closing every window never quits (Mac rule 4): the app lives in the tray until Quit Spit.
public partial class App : Application
{
    private readonly SingleInstance? instance;
    private UiShell? shell;
    private Coordinator? coordinator;

    public App()
    {
    }

    internal App(SingleInstance instance) => this.instance = instance;

    /// Mac `applicationDidFinishLaunching`. Nothing here may stop Spit from running: a CI runner has no
    /// microphone, no token and no model, and a friend's PC may lack any of them too.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var paths = AppPaths.Default;
        try
        {
            Directory.CreateDirectory(paths.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Failure("app", "create the data folder", ex);
        }
        Log.Paths = paths;
        Log.Info("app", $"Spit {Coordinator.ClientVersion} starting on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");

        var settings = new SettingsStore(paths);
        var model = new AppModel(settings);
        shell = new UiShell();
        try
        {
            coordinator = new Coordinator(model, settings, paths, shell);
        }
        catch (Exception ex)
        {
            Log.Crash("app", "build the coordinator", ex);
        }

        if (coordinator is { } started)
        {
            Attempt("show the UI", () => shell.Start(model, settings, () => started.Api, started.Insights));
            Attempt("start the coordinator", started.Start);
        }
        if (instance is not null) instance.Activated += () => shell?.ShowMain();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("app", "exiting");
        coordinator?.Dispose();
        shell?.Dispose();
        base.OnExit(e);
    }

    private static void Attempt(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Log.Crash("app", what, ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Crash("app", "unhandled on the UI thread", e.Exception);
        // No one sees a crash dialog from a tray app, and a dead Spit silently stops the hotkey working.
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) Log.Crash("app", e.IsTerminating ? "unhandled, terminating" : "unhandled", ex);
        else Log.Error("app", "unhandled throw of a non-exception object");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Crash("app", "unobserved task failure", e.Exception);
        e.SetObserved();
    }
}

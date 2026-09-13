using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Spit.App;

/// The desktop app: Insights and Settings in one window, chosen from a sidebar (Mac `MainWindow`).
/// Closing hides it and never quits (rule 37, Mac rule 4): Spit is a dictation service that happens to
/// have a window. WPF's default taskbar behaviour gives it a button only while it is shown.
public partial class MainWindow : Window
{
    private readonly InsightsPage insights;
    private readonly SettingsPage settings;
    private bool allowClose;

    public MainWindow(InsightsPage insights, SettingsPage settings)
    {
        ArgumentNullException.ThrowIfNull(insights);
        ArgumentNullException.ThrowIfNull(settings);
        this.insights = insights;
        this.settings = settings;
        ThemePalette.Attach(this);
        InitializeComponent();
        SetWindowIcon(this);
        Nav.SelectedItem = NavInsights;
        Detail.Content = insights;
    }

    public MainSection Section => ReferenceEquals(Nav.SelectedItem, NavSettings) ? MainSection.Settings : MainSection.Insights;

    /// Brings the window to the front, optionally on a section and a settings tab.
    public void ShowSection(MainSection? section = null, SettingsSection? tab = null)
    {
        if (tab is { } t)
        {
            section = MainSection.Settings;
            settings.SelectTab(t);
        }
        if (section is { } s) Nav.SelectedItem = s == MainSection.Settings ? NavSettings : NavInsights;

        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Activate() alone can leave the window behind the foreground app when Windows' foreground lock
        // applies (a signal from a second instance); a topmost blip brings it forward without keeping it there.
        Topmost = true;
        Topmost = false;
    }

    public void CloseForShutdown()
    {
        allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    internal static void SetWindowIcon(Window window)
    {
        try
        {
            window.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Spit;component/Assets/Spit.ico", UriKind.Absolute));
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            // The window keeps the exe's icon.
            Log.Failure("ui", "window icon", e);
        }
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is null)
        {
            // The sidebar always has a selection, like the Mac's `section ?? .insights`.
            Nav.SelectedItem = NavInsights;
            return;
        }
        Detail.Content = Section == MainSection.Settings ? settings : insights;
    }
}

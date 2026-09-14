namespace Spit.App;

/// The two things the main window is, as on the Mac's `MainWindow.Section`.
public enum MainSection
{
    Insights,
    Settings,
}

/// The Settings pane's tabs, in `SettingsTabs` order (the Mac's Permissions tab is replaced by the
/// Set-up window on Windows, rule 40).
public enum SettingsSection
{
    General,
    Model,
    Server,
    Dictionary,
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Spit.Core;

namespace Spit.App;

/// The tray icon: the Windows form of the Mac's `MenuBarExtra` (rule 37). Left-click opens the main
/// window; right-click shows the menu in exactly the Mac's order. The icon follows `menuIcon`'s ranking
/// through `AppModel.TrayIconState`.
public sealed class TrayIcon : IDisposable
{
    private readonly AppModel model;
    private readonly Action openMainWindow;
    private readonly TaskbarIcon icon;
    private readonly Dictionary<MenuIconState, System.Drawing.Icon> rendered = [];
    private System.Drawing.Icon? fallback;

    private readonly MenuItem statusItem;
    private readonly MenuItem modelStatusItem;
    private readonly MenuItem modeClean;
    private readonly MenuItem modeLiteral;
    private readonly MenuItem langAuto;
    private readonly MenuItem langPt;
    private readonly MenuItem langEn;
    private readonly MenuItem copyLast;
    private readonly MenuItem pauseResume;
    private readonly MenuItem showBar;
    private readonly MenuItem liveTranscription;

    private MenuIconState? shownState;
    private bool shownLightTaskbar;
    private bool disposed;

    public TrayIcon(AppModel model, Action openMainWindow)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(openMainWindow);
        this.model = model;
        this.openMainWindow = openMainWindow;

        statusItem = Info();
        modelStatusItem = Info();

        modeClean = Choice(Strings.ModeClean, () => model.RequestMode("clean"));
        modeLiteral = Choice(Strings.ModeLiteral, () => model.RequestMode("literal"));
        var mode = Submenu(Strings.Mode, modeClean, modeLiteral);

        langAuto = Choice(Strings.LangAuto, () => model.RequestLanguage("auto"));
        langPt = Choice(Strings.LangPt, () => model.RequestLanguage("pt"));
        langEn = Choice(Strings.LangEn, () => model.RequestLanguage("en"));
        var language = Submenu(Strings.Language, langAuto, langPt, langEn);

        copyLast = Action(Strings.CopyLast, () => model.RequestCopyLastDictation());
        pauseResume = Action(Strings.Pause, () => model.RequestTogglePause());
        showBar = Action(Strings.ShowBar, () => model.RequestShowBar(!model.Settings.ShowBar));
        liveTranscription = Action(Strings.LiveTranscription, () => model.RequestLiveTranscription(!model.Settings.LiveTranscription));

        var menu = new ContextMenu();
        // R37 order, item for item with VoiceApp.swift's MenuBarExtra.
        menu.Items.Add(statusItem);
        menu.Items.Add(modelStatusItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(mode);
        menu.Items.Add(language);
        menu.Items.Add(new Separator());
        menu.Items.Add(copyLast);
        menu.Items.Add(pauseResume);
        menu.Items.Add(showBar);
        menu.Items.Add(liveTranscription);
        menu.Items.Add(new Separator());
        menu.Items.Add(Action(Strings.InsightsMenuItem, () => model.RequestOpenInsights()));
        menu.Items.Add(Action(Strings.SetUpPermissions, () => model.RequestOpenSetup()));
        menu.Items.Add(Action(Strings.Settings, () => model.RequestOpenSettings(SettingsSection.General)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Action(Strings.Quit, () => model.RequestQuit()));
        menu.Opened += (_, _) => RefreshMenu();

        icon = new TaskbarIcon
        {
            ToolTipText = Strings.AppName,
            ContextMenu = menu,
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };
        icon.TrayLeftMouseUp += (_, _) => this.openMainWindow();

        RefreshMenu();
        RefreshIcon();
        model.PropertyChanged += OnModelChanged;
        ThemePalette.Changed += OnThemeChanged;
    }

    /// Adds the icon to the notification area. Efficiency Mode stays off: it lowers the process's
    /// priority, and the keyboard hook and audio capture must not be throttled.
    public void Show() => icon.ForceCreate(enablesEfficiencyMode: false);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        model.PropertyChanged -= OnModelChanged;
        ThemePalette.Changed -= OnThemeChanged;
        icon.Dispose();
        foreach (var i in rendered.Values) i.Dispose();
        rendered.Clear();
        fallback?.Dispose();
    }

    private void OnThemeChanged()
    {
        foreach (var i in rendered.Values) i.Dispose();
        rendered.Clear();
        shownState = null;
        RefreshIcon();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppModel.TrayIconState):
                RefreshIcon();
                break;
            case nameof(AppModel.Paused) or nameof(AppModel.StatusLine) or nameof(AppModel.ModelStatus)
                or nameof(AppModel.Settings) or nameof(AppModel.LastText):
                RefreshMenu();
                break;
        }
    }

    private void RefreshMenu()
    {
        var s = model.Settings;
        statusItem.Header = Plain(model.Paused ? Strings.Pause : model.StatusLine);
        modelStatusItem.Header = Plain(model.ModelStatus);
        modeClean.IsChecked = s.Mode == "clean";
        modeLiteral.IsChecked = s.Mode == "literal";
        langAuto.IsChecked = s.Language == "auto";
        langPt.IsChecked = s.Language == "pt";
        langEn.IsChecked = s.Language == "en";
        copyLast.IsEnabled = model.LastText is not null;
        pauseResume.Header = Plain(model.Paused ? Strings.Resume : Strings.Pause);
        showBar.IsChecked = s.ShowBar;
        liveTranscription.IsChecked = s.LiveTranscription;
    }

    private void RefreshIcon()
    {
        var state = model.TrayIconState;
        var light = ThemePalette.TaskbarIsLight;
        if (shownState == state && shownLightTaskbar == light) return;
        if (!rendered.TryGetValue(state, out var image))
        {
            try
            {
                image = TrayIconRenderer.Render(state, light);
                rendered[state] = image;
            }
            catch (Exception e)
            {
                Log.Failure("tray", "render icon", e);
                image = fallback ??= TrayIconRenderer.AppIcon();
            }
        }
        if (image is null) return;
        icon.Icon = image;
        shownState = state;
        shownLightTaskbar = light;
    }

    /// Menu headers go through a TextBlock so an underscore in a status message is shown, not taken as
    /// an access key.
    private static TextBlock Plain(string text) => new() { Text = text };

    private static MenuItem Info() => new() { IsEnabled = false };

    private static MenuItem Submenu(string header, params MenuItem[] items)
    {
        var item = new MenuItem { Header = Plain(header) };
        foreach (var child in items) item.Items.Add(child);
        return item;
    }

    private static MenuItem Choice(string header, Func<Task> onClick)
    {
        var item = new MenuItem { Header = Plain(header), IsCheckable = false };
        item.Click += async (_, _) => await onClick();
        return item;
    }

    private static MenuItem Action(string header, Func<Task> onClick)
    {
        var item = new MenuItem { Header = Plain(header) };
        item.Click += async (_, _) => await onClick();
        return item;
    }
}

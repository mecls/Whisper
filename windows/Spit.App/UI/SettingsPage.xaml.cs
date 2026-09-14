using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Spit.Core;

namespace Spit.App;

/// General, Model, Server and Dictionary (Mac `SettingsTabs`). A setting applies the moment it changes;
/// buttons are disabled while their request runs; failures are one line of text under the control.
///
/// State flows one way: `Sync()` copies the model onto the controls under a guard, and control events
/// call the model's `Request…` methods. No XAML bindings — this app is compiled on a Mac and cannot be
/// run there, so every name a view reads is checked by the compiler instead of failing silently at
/// runtime.
public partial class SettingsPage : UserControl, IDisposable
{
    private static readonly (string Tag, string Label)[] Languages =
        [("auto", Strings.LangAuto), ("pt", Strings.LangPt), ("en", Strings.LangEn)];

    private readonly AppModel model;
    private readonly Func<IVoiceApiClient> api;
    private readonly LaunchAtLogin launchAtLogin;
    private bool syncing;
    private bool disposed;
    private bool serverBusy;
    private bool dictionaryBusy;
    private SettingsSection tab = SettingsSection.General;

    public SettingsPage(AppModel model, Func<IVoiceApiClient> api, LaunchAtLogin launchAtLogin)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(launchAtLogin);
        this.model = model;
        this.api = api;
        this.launchAtLogin = launchAtLogin;
        InitializeComponent();

        syncing = true;
        foreach (var choice in HotkeyChoiceExtensions.AllCases) HotkeyCombo.Items.Add(new ComboBoxItem { Content = choice.Label(), Tag = choice });
        foreach (var entry in ModelCatalog.Default.Entries) ModelCombo.Items.Add(new ComboBoxItem { Content = entry.Label, Tag = entry.File });
        foreach (var (tag, label) in Languages) LanguageCombo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        TabGeneral.IsChecked = true;
        syncing = false;

        model.PropertyChanged += OnModelPropertyChanged;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) OnAppeared();
        };
        Sync();
    }

    /// Switches to `section`'s tab (the Insights notice's link, the tray's Settings…).
    public void SelectTab(SettingsSection section)
    {
        var button = section switch
        {
            SettingsSection.Model => TabModel,
            SettingsSection.Server => TabServer,
            SettingsSection.Dictionary => TabDictionary,
            _ => TabGeneral,
        };
        if (button.IsChecked == true) OnTabShown(section);
        else button.IsChecked = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        model.PropertyChanged -= OnModelPropertyChanged;
    }

    /// Levels arrive ~47 times a second while listening; only the properties this page shows re-sync it.
    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.Settings) or nameof(AppModel.ActiveModelLabel) or nameof(AppModel.ModelStatus)
            or nameof(AppModel.ModelDownloadProgress) or nameof(AppModel.ModelDownloaded) or nameof(AppModel.UserName)
            or nameof(AppModel.Unauthorized) or nameof(AppModel.HasToken) or nameof(AppModel.TokenChecking))
        {
            Sync();
        }
    }

    private void OnAppeared() => OnTabShown(tab);

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        var section = ReferenceEquals(sender, TabModel) ? SettingsSection.Model
            : ReferenceEquals(sender, TabServer) ? SettingsSection.Server
            : ReferenceEquals(sender, TabDictionary) ? SettingsSection.Dictionary
            : SettingsSection.General;
        OnTabShown(section);
    }

    private void OnTabShown(SettingsSection section)
    {
        tab = section;
        GeneralPanel.Visibility = section == SettingsSection.General ? Visibility.Visible : Visibility.Collapsed;
        ModelPanel.Visibility = section == SettingsSection.Model ? Visibility.Visible : Visibility.Collapsed;
        ServerPanel.Visibility = section == SettingsSection.Server ? Visibility.Visible : Visibility.Collapsed;
        DictionaryPanel.Visibility = section == SettingsSection.Dictionary ? Visibility.Visible : Visibility.Collapsed;
        if (!IsVisible) return;
        switch (section)
        {
            case SettingsSection.General:
                _ = ReadLaunchAtLoginAsync();
                break;
            case SettingsSection.Dictionary:
                _ = LoadTermsAsync();
                break;
        }
    }

    /// Copies the model onto every control. Runs under `syncing` so the controls' own change events don't
    /// write the values straight back.
    private void Sync()
    {
        syncing = true;
        try
        {
            var s = model.Settings;

            // General
            SelectByTag(HotkeyCombo, model.Hotkey);
            SoundsCheck.IsChecked = s.Sounds;
            ShowTextCheck.IsChecked = s.ShowTextInHUD;
            ShowBarCheck.IsChecked = s.ShowBar;
            LiveCheck.IsChecked = s.LiveTranscription;

            // Model
            SelectByTag(ModelCombo, s.ModelFile);
            ActiveModelText.Text = Strings.ActiveModel(model.ActiveModelLabel);
            ModelStatusText.Text = model.ModelStatus;
            var downloading = model.ModelDownloadProgress is not null;
            DownloadProgressPanel.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
            if (model.ModelDownloadProgress is { } p)
            {
                DownloadProgress.Value = Math.Clamp(p, 0, 1);
                DownloadProgressText.Text = Strings.ModelDownloading((int)(Math.Clamp(p, 0, 1) * 100));
            }
            DownloadButton.IsEnabled = !model.ModelDownloaded && !downloading;
            // Mac H3: never delete the model actually loaded.
            DeleteButton.IsEnabled = !downloading && AppModel.LabelFor(s.ModelFile) != model.ActiveModelLabel;
            SelectByTag(LanguageCombo, s.Language);

            // Server
            if (!ServerUrlBox.IsKeyboardFocusWithin) ServerUrlBox.Text = s.ServerURL;
            ConnectionStatus.Text = ConnectionLine();
            ConnectionStatus.SetResourceReference(TextBlock.ForegroundProperty,
                model.UserName is not null && !model.Unauthorized ? "Spit.Success" : "Spit.Text");
            SaveTokenButton.IsEnabled = !serverBusy && TokenBox.Password.Length > 0;
            TestConnectionButton.IsEnabled = !serverBusy;
            SignOutButton.IsEnabled = !serverBusy;
        }
        finally
        {
            syncing = false;
        }
    }

    private string ConnectionLine()
    {
        if (model.Unauthorized && model.HasToken) return Strings.TokenInvalid;
        if (model.UserName is { } name && !model.Unauthorized) return Strings.ConnectedAs(name);
        if (model.TokenChecking) return Strings.SetupTokenChecking;
        return Strings.NotConnected;
    }

    // MARK: - General

    private void OnHotkeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || HotkeyCombo.SelectedItem is not ComboBoxItem { Tag: HotkeyChoice choice }) return;
        _ = model.RequestHotkey(choice);
    }

    private void OnSoundsClick(object sender, RoutedEventArgs e)
    {
        if (!syncing) model.SetSounds(SoundsCheck.IsChecked == true);
    }

    private void OnShowTextClick(object sender, RoutedEventArgs e)
    {
        if (!syncing) model.SetShowTextInHUD(ShowTextCheck.IsChecked == true);
    }

    private void OnShowBarClick(object sender, RoutedEventArgs e)
    {
        if (!syncing) _ = model.RequestShowBar(ShowBarCheck.IsChecked == true);
    }

    private void OnLiveClick(object sender, RoutedEventArgs e)
    {
        if (!syncing) _ = model.RequestLiveTranscription(LiveCheck.IsChecked == true);
    }

    /// The toggle shows the registry's real value, read each time the tab appears — off the UI thread,
    /// because a registry read has no business in front of a window being painted.
    private async Task ReadLaunchAtLoginAsync()
    {
        var enabled = await Task.Run(launchAtLogin.IsEnabled);
        syncing = true;
        LaunchCheck.IsChecked = enabled;
        syncing = false;
    }

    private async void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (syncing) return;
        var on = LaunchCheck.IsChecked == true;
        LaunchCheck.IsEnabled = false;
        var failure = await model.RequestLaunchAtLogin(on);
        LaunchCheck.IsEnabled = true;
        if (failure is not null)
        {
            // The switch must not claim a state it failed to reach.
            syncing = true;
            LaunchCheck.IsChecked = !on;
            syncing = false;
            ShowError(LaunchError, Strings.LaunchAtLoginError(failure));
        }
        else
        {
            ShowError(LaunchError, null);
        }
    }

    // MARK: - Model

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || ModelCombo.SelectedItem is not ComboBoxItem { Tag: string file }) return;
        ShowError(DeleteError, null);
        _ = model.RequestModelFile(file);
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        DownloadButton.IsEnabled = false;
        _ = model.RequestDownloadModel(model.Settings.ModelFile);
    }

    private void OnReloadClick(object sender, RoutedEventArgs e) => _ = model.RequestReloadModel();

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        DeleteButton.IsEnabled = false;
        var error = await model.RequestDeleteModel(model.Settings.ModelFile);
        ShowError(DeleteError, error);
        Sync();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        _ = model.RequestLanguage(language);
    }

    // MARK: - Server

    private void OnServerUrlCommit(object sender, KeyboardFocusChangedEventArgs e) => CommitServerUrl();

    private void OnServerUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitServerUrl();
    }

    private void CommitServerUrl()
    {
        if (syncing) return;
        var url = ServerUrlBox.Text.Trim();
        if (url.Length > 0 && url != model.Settings.ServerURL) model.SetServerURL(url);
    }

    private void OnTokenChanged(object sender, RoutedEventArgs e) =>
        SaveTokenButton.IsEnabled = !serverBusy && TokenBox.Password.Length > 0;

    private async void OnSaveTokenClick(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password;
        if (token.Length == 0) return;
        // Clear the field first: once saved, the token lives only in Credential Manager (Mac H4).
        TokenBox.Clear();
        await RunServerAction(async () => ShowError(SaveTokenError, await model.RequestSaveToken(token)));
    }

    private async void OnTestConnectionClick(object sender, RoutedEventArgs e) =>
        await RunServerAction(() => model.RequestTestConnection());

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        TokenBox.Clear();
        await RunServerAction(() => model.RequestSignOut());
    }

    private async Task RunServerAction(Func<Task> action)
    {
        serverBusy = true;
        Sync();
        try
        {
            await action();
        }
        finally
        {
            serverBusy = false;
            Sync();
        }
    }

    // MARK: - Dictionary (calls the API directly, as the Mac's DictionaryTab does)

    private async Task LoadTermsAsync()
    {
        if (dictionaryBusy) return;
        dictionaryBusy = true;
        DictionaryLoading.Visibility = Visibility.Visible;
        try
        {
            var entries = await api().ListTermsAsync();
            ShowTerms(entries);
            ShowError(DictionaryError, null);
        }
        catch (Exception e)
        {
            Log.Failure("dictionary", "list terms", e);
            ShowError(DictionaryError, Strings.DictionaryLoadError);
        }
        finally
        {
            DictionaryLoading.Visibility = Visibility.Collapsed;
            dictionaryBusy = false;
            UpdateAddEnabled();
        }
    }

    private void ShowTerms(IReadOnlyList<DictionaryEntry> entries)
    {
        TermsList.Children.Clear();
        DictionaryEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var entry in entries) TermsList.Children.Add(TermRow(entry));
    }

    private FrameworkElement TermRow(DictionaryEntry entry)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = true };

        var delete = new Button { Content = "", VerticalAlignment = VerticalAlignment.Center };
        delete.SetResourceReference(StyleProperty, "Spit.IconButton");
        AutomationProperties.SetName(delete, Strings.DeleteModel);
        delete.Click += async (_, _) => await DeleteTermAsync(entry);
        DockPanel.SetDock(delete, Dock.Right);
        row.Children.Add(delete);

        var text = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock
        {
            Text = entry.Replacement is { } r ? Strings.DictionaryReplacement(entry.Term, r) : entry.Term,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Spit.Text");
        text.Children.Add(label);
        if (entry.TeamWide)
        {
            var tag = new Border
            {
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(6, 1, 6, 1),
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = Strings.DictionaryTeamWide, FontSize = 11 },
            };
            tag.SetResourceReference(Border.BackgroundProperty, "Spit.Selection");
            ((TextBlock)tag.Child).SetResourceReference(TextBlock.ForegroundProperty, "Spit.TextSecondary");
            text.Children.Add(tag);
        }
        row.Children.Add(text);
        return row;
    }

    private void OnNewTermChanged(object sender, TextChangedEventArgs e) => UpdateAddEnabled();

    private void UpdateAddEnabled() =>
        AddTermButton.IsEnabled = !dictionaryBusy && NewTermBox.Text.Trim().Length > 0;

    private async void OnAddTermClick(object sender, RoutedEventArgs e)
    {
        var term = NewTermBox.Text.Trim();
        if (term.Length == 0 || dictionaryBusy) return;
        var replacement = NewReplacementBox.Text;
        var teamWide = TeamWideCheck.IsChecked == true;
        dictionaryBusy = true;
        UpdateAddEnabled();
        try
        {
            await api().AddTermAsync(term, replacement.Length == 0 ? null : replacement, teamWide);
            NewTermBox.Clear();
            NewReplacementBox.Clear();
            TeamWideCheck.IsChecked = false;
            ShowError(DictionaryError, null);
        }
        catch (Exception ex)
        {
            Log.Failure("dictionary", "add term", ex);
            ShowError(DictionaryError, Strings.DictionaryAddError);
            dictionaryBusy = false;
            UpdateAddEnabled();
            return;
        }
        dictionaryBusy = false;
        // Refreshes the dictionary cache so the next dictation sees the new term, then the list.
        await model.RequestDictionaryChanged();
        await LoadTermsAsync();
    }

    private async Task DeleteTermAsync(DictionaryEntry entry)
    {
        if (dictionaryBusy) return;
        dictionaryBusy = true;
        try
        {
            await api().DeleteTermAsync(entry.Id);
            ShowError(DictionaryError, null);
        }
        catch (Exception ex)
        {
            Log.Failure("dictionary", "delete term", ex);
            ShowError(DictionaryError, Strings.DictionaryDeleteError);
            dictionaryBusy = false;
            UpdateAddEnabled();
            return;
        }
        dictionaryBusy = false;
        await model.RequestDictionaryChanged();
        await LoadTermsAsync();
    }

    // MARK: - Helpers

    private static void ShowError(TextBlock target, string? text)
    {
        target.Text = text ?? "";
        target.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SelectByTag(ComboBox combo, object tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem c && Equals(c.Tag, tag))
            {
                if (!ReferenceEquals(combo.SelectedItem, c)) combo.SelectedItem = c;
                return;
            }
        }
        combo.SelectedItem = null;
    }
}

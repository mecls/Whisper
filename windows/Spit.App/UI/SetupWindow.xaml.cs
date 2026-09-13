using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Spit.Core;

namespace Spit.App;

/// Set-up (rule 40), the Windows form of the Mac's `OnboardingView` + `PermissionsView`: Microphone,
/// Hotkey and Token, each updating live from the model. Done is enabled when Microphone and Hotkey are
/// green, sets `onboarded` and closes. Closing hides the window, so "Set up…" can bring it back.
public partial class SetupWindow : Window
{
    private readonly AppModel model;
    private bool allowClose;

    public SetupWindow(AppModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        this.model = model;
        ThemePalette.Attach(this);
        InitializeComponent();
        MainWindow.SetWindowIcon(this);
        model.PropertyChanged += OnModelChanged;
        Render();
    }

    public void ShowAndActivate()
    {
        Render();
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
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

    protected override void OnClosed(EventArgs e)
    {
        model.PropertyChanged -= OnModelChanged;
        base.OnClosed(e);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.Microphone) or nameof(AppModel.HotkeySeen) or nameof(AppModel.Settings)
            or nameof(AppModel.UserName) or nameof(AppModel.Unauthorized) or nameof(AppModel.HasToken)
            or nameof(AppModel.TokenChecking))
        {
            Render();
        }
    }

    private void Render()
    {
        var micOk = model.Microphone == MicrophoneState.Available;
        MicText.Text = model.Microphone switch
        {
            MicrophoneState.Available => Strings.SetupMicrophoneGranted,
            MicrophoneState.Blocked => Strings.SetupMicrophoneBlocked,
            MicrophoneState.NoDevice => Strings.NoMicrophone,
            _ => Strings.SetupMicrophone,
        };
        SetMark(MicMark, micOk ? Mark.Ok : model.Microphone is MicrophoneState.Blocked or MicrophoneState.NoDevice ? Mark.Problem : Mark.Pending);
        MicButton.Visibility = model.Microphone == MicrophoneState.Blocked ? Visibility.Visible : Visibility.Collapsed;

        var key = model.Hotkey.Label();
        HotkeyText.Text = model.HotkeySeen ? Strings.SetupHotkeySeen(key) : Strings.SetupHotkey(key);
        SetText(HotkeyText, model.HotkeySeen ? "Spit.Success" : "Spit.Text");
        SetMark(HotkeyMark, model.HotkeySeen ? Mark.Ok : Mark.Pending);

        var connected = model.UserName is not null && !model.Unauthorized;
        if (connected)
        {
            TokenText.Text = Strings.ConnectedAs(model.UserName!);
            SetMark(TokenMark, Mark.Ok);
        }
        else if (model.TokenChecking)
        {
            TokenText.Text = Strings.SetupTokenChecking;
            SetMark(TokenMark, Mark.Pending);
        }
        else if (model.Unauthorized && model.HasToken)
        {
            TokenText.Text = Strings.TokenInvalid;
            SetMark(TokenMark, Mark.Problem);
        }
        else
        {
            TokenText.Text = Strings.SetupTokenMissing;
            SetMark(TokenMark, Mark.Pending);
        }
        SetText(TokenText, connected ? "Spit.Success" : "Spit.Text");
        TokenButton.Visibility = connected || model.TokenChecking ? Visibility.Collapsed : Visibility.Visible;

        DoneButton.IsEnabled = micOk && model.HotkeySeen;
    }

    private void OnOpenMicrophoneSettings(object sender, RoutedEventArgs e) => _ = model.RequestOpenMicrophoneSettings();

    private void OnOpenServerSettings(object sender, RoutedEventArgs e) => _ = model.RequestOpenSettings(SettingsSection.Server);

    private void OnDone(object sender, RoutedEventArgs e)
    {
        model.CompleteOnboarding();
        Close();
    }

    private enum Mark { Pending, Ok, Problem }

    private static void SetText(TextBlock text, string brushKey) =>
        text.SetResourceReference(TextBlock.ForegroundProperty, brushKey);

    /// Green filled circle with a tick, grey ring, or a red ring — the Mac's `checkmark.circle.fill` / `circle`.
    private static void SetMark(Grid host, Mark mark)
    {
        host.Children.Clear();
        var circle = new Ellipse { Width = 16, Height = 16, StrokeThickness = 1.5 };
        switch (mark)
        {
            case Mark.Ok:
                circle.SetResourceReference(Shape.FillProperty, "Spit.Success");
                host.Children.Add(circle);
                var tick = new Path
                {
                    Data = Geometry.Parse("M4.5,8.5 L7,11 L11.5,5.5"),
                    Stroke = Brushes.White,
                    StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                };
                host.Children.Add(tick);
                break;
            case Mark.Problem:
                circle.SetResourceReference(Shape.StrokeProperty, "Spit.Danger");
                host.Children.Add(circle);
                break;
            default:
                circle.SetResourceReference(Shape.StrokeProperty, "Spit.TextTertiary");
                host.Children.Add(circle);
                break;
        }
    }
}

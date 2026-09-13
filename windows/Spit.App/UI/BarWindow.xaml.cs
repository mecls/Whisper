using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Spit.Core;

namespace Spit.App;

/// The bar: always on screen (unless Show bar is off), as small as its contents, and never activating
/// Spit (rule 38). Port of `HUDPanel` + `HUDView`.
///
/// Activation is prevented three ways, because each alone has a gap: `ShowActivated=false` for the first
/// `Show()`, `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` from `SourceInitialized`, and `MA_NOACTIVATE` for
/// `WM_MOUSEACTIVATE`. Clicks land on non-focusable borders, so WPF never calls `SetFocus` either. If a
/// click activated Spit, the dictation's target would become Spit itself and the text would be diverted
/// to the clipboard (rule 34).
public partial class BarWindow : Window
{
    private const int BarCount = 20;
    private const double BarWidth = 2.5;
    private const double BarGap = 1.5;
    private const double BarMax = 16;
    private const int LiveTextChars = 44;
    private static readonly nint HwndTopmost = -1;

    /// The WinEvent callback is static (an unmanaged entry point); it forwards to the one bar.
    private static BarWindow? current;

    private readonly AppModel model;
    private readonly Rectangle[] bars = new Rectangle[BarCount];
    private nint hwnd;
    private nint foregroundHook;
    private bool allowClose;

    public BarWindow(AppModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        this.model = model;
        Resources.MergedDictionaries.Add(ThemePalette.Resources);
        InitializeComponent();

        for (var i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = BarWidth,
                Height = 2,
                RadiusX = 1,
                RadiusY = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, i == BarCount - 1 ? 0 : BarGap, 0),
            };
            bar.SetResourceReference(Shape.FillProperty, "Spit.BarLevel");
            bars[i] = bar;
            Waveform.Children.Add(bar);
        }

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition();
        DpiChanged += (_, _) => Reposition();
        model.PropertyChanged += OnModelChanged;
        Render();
    }

    /// Shows or hides the bar per Show bar; called at start and whenever the setting changes.
    public void ApplyVisibility()
    {
        if (!model.Settings.ShowBar)
        {
            if (IsVisible) Hide();
            return;
        }
        if (!IsVisible)
        {
            // Create the handle first so the extended styles are on before the window is ever visible.
            new WindowInteropHelper(this).EnsureHandle();
            Show();
        }
        RepositionAfterLayout();
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
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        model.PropertyChanged -= OnModelChanged;
        if (foregroundHook != 0) UiNative.UnhookWinEvent(foregroundHook);
        foregroundHook = 0;
        if (ReferenceEquals(current, this)) current = null;
        base.OnClosed(e);
    }

    private unsafe void OnSourceInitialized(object? sender, EventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle;
        var style = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        if (Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (nint)(style | UiNative.WS_EX_NOACTIVATE | UiNative.WS_EX_TOOLWINDOW)) == 0
            && Marshal.GetLastPInvokeError() is var error and not 0)
        {
            Log.Win32Failure("bar", "SetWindowLongPtr", error);
        }
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        // The bar follows keyboard focus across displays, like the Mac's `didActivateApplication`
        // observer — not the mouse, which would make an always-visible bar jump as the pointer moved.
        current = this;
        foregroundHook = UiNative.SetWinEventHook(UiNative.EVENT_SYSTEM_FOREGROUND, UiNative.EVENT_SYSTEM_FOREGROUND, 0,
            &OnForegroundChanged, 0, 0, UiNative.WINEVENT_OUTOFCONTEXT);
        if (foregroundHook == 0) Log.Error("bar", "SetWinEventHook failed; the bar moves between displays only on state changes");
    }

    [UnmanagedCallersOnly]
    private static void OnForegroundChanged(nint hook, uint eventType, nint window, int idObject, int idChild, uint thread, uint time)
    {
        current?.Reposition();
    }

    private nint WndProc(nint window, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == UiNative.WM_MOUSEACTIVATE)
        {
            handled = true;
            return UiNative.MA_NOACTIVATE;
        }
        return 0;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppModel.Levels):
                UpdateBars();
                break;
            case nameof(AppModel.Hud) or nameof(AppModel.IsLatched) or nameof(AppModel.LiveText):
                Render();
                RepositionAfterLayout();
                break;
            case nameof(AppModel.Settings):
                ApplyVisibility();
                Render();
                break;
        }
    }

    private void OnMicClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ = model.RequestMicTap();
    }

    private void OnOpenMicrophoneSettings(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ = model.RequestOpenMicrophoneSettings();
    }

    private void Render()
    {
        var hud = model.Hud;
        var latched = model.IsLatched;

        if (hud is HUDState.Hidden)
        {
            IdlePill.Visibility = Visibility.Visible;
            ActivePill.Visibility = Visibility.Collapsed;
            return;
        }
        IdlePill.Visibility = Visibility.Collapsed;
        ActivePill.Visibility = Visibility.Visible;

        // The mic: red with a stop square while latched — the bar's only latched indicator.
        MicGlyph.Visibility = latched ? Visibility.Collapsed : Visibility.Visible;
        StopGlyph.Visibility = latched ? Visibility.Visible : Visibility.Collapsed;
        MicButton.SetResourceReference(Border.BackgroundProperty, latched ? "Spit.Recording" : "Spit.BarMicFill");
        MicButton.ToolTip = latched ? Strings.Latched : Strings.AppName;
        foreach (var bar in bars) bar.SetResourceReference(Shape.FillProperty, latched ? "Spit.RecordingLevel" : "Spit.BarLevel");

        var live = model.LiveText;
        if (hud is HUDState.Listening)
        {
            // A moving waveform already says "recording" and "hearing you"; no status word beside it.
            Waveform.Visibility = Visibility.Visible;
            UpdateBars();
            TitleText.Visibility = Visibility.Collapsed;
            SetDetail(live is null ? null : HeadTruncate(live));
            StatusPanel.Visibility = live is null ? Visibility.Collapsed : Visibility.Visible;
            OpenSettingsLink.Visibility = Visibility.Collapsed;
            return;
        }

        Waveform.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
        TitleText.Visibility = Visibility.Visible;
        TitleText.Text = StatusTitle(hud);
        if (live is not null) SetDetail(HeadTruncate(live));
        else if (hud is HUDState.Done { Preview: { } preview } && model.Settings.ShowTextInHUD) SetDetail(preview);
        else SetDetail(null);
        OpenSettingsLink.Visibility = hud is HUDState.Message { Text: Strings.MicrophoneBlocked }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetDetail(string? text)
    {
        DetailText.Text = text ?? "";
        DetailText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// `HUDView.title`.
    private static string StatusTitle(HUDState hud) => hud switch
    {
        HUDState.Transcribing { Progress: { } p } => $"{Strings.Transcribing} {(int)(p * 100)} %",
        HUDState.Transcribing => Strings.Transcribing,
        HUDState.Cleaning => Strings.Cleaning,
        HUDState.Done { Via: { } via } => $"{Strings.Done} · {via}",
        HUDState.Done => Strings.Done,
        HUDState.Message m => m.Text,
        HUDState.ModelLoading l => $"{Strings.ModelLoading} {(int)(l.Progress * 100)} %",
        _ => "",
    };

    /// Always exactly 20 bars, zero-padded at the front, so the bar never resizes as samples arrive.
    private void UpdateBars()
    {
        var levels = model.Levels;
        var recent = levels.Count > BarCount ? levels.Skip(levels.Count - BarCount).ToArray() : levels.ToArray();
        var pad = BarCount - recent.Length;
        for (var i = 0; i < BarCount; i++)
        {
            var level = i < pad ? 0f : recent[i - pad];
            bars[i].Height = Math.Max(2, Math.Min(level * 40, 1) * BarMax);
        }
    }

    /// Live text keeps its newest words: the Mac truncates at the head.
    private static string HeadTruncate(string text) =>
        text.Length <= LiveTextChars ? text : "…" + text[^(LiveTextChars - 1)..];

    private void RepositionAfterLayout() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Reposition);

    /// Places the bar on the monitor holding the foreground window, from one measurement of its current
    /// size (`HUDPanel.layoutToFit`). Positions are physical pixels, so the move goes through
    /// `SetWindowPos` rather than WPF's DPI-dependent `Left`/`Top`; a move onto a monitor with another
    /// DPI makes WPF rescale the window, and `DpiChanged` places it again at its new pixel size.
    private void Reposition()
    {
        if (hwnd == 0 || !IsVisible) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (width < 1 || height < 1) return;

        var foreground = Native.GetForegroundWindow();
        var monitor = Native.MonitorFromWindow(foreground,
            foreground == 0 ? UiNative.MONITOR_DEFAULTTOPRIMARY : Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        if (monitor == 0 || !Native.GetMonitorInfo(monitor, ref info)) return;

        var (x, y) = BarPlacement.Compute(ToRect(info.rcMonitor), ToRect(info.rcWork), width, height);
        if (UiNative.GetWindowRect(hwnd, out var now) && now.left == x && now.top == y) return;
        // HWND_TOPMOST again on every move: another topmost window may have been raised above the bar.
        if (!UiNative.SetWindowPos(hwnd, HwndTopmost, x, y, 0, 0, UiNative.SWP_NOSIZE | UiNative.SWP_NOACTIVATE))
            Log.Win32Failure("bar", "SetWindowPos", Marshal.GetLastPInvokeError());
    }

    private static ScreenRect ToRect(Native.RECT r) => new(r.left, r.top, r.right, r.bottom);
}

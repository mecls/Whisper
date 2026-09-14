using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Spit.App;

/// Light, dark and high-contrast palettes over one shared `ThemeDictionary`. Windows' app-mode setting
/// (`AppsUseLightTheme`) picks light or dark; high contrast uses the system colours outright; the accent
/// is the user's DWM accent colour. Changes apply live: every brush is read through `DynamicResource`.
internal static class ThemePalette
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    private static ThemeDictionary? shared;

    /// Raised on the UI thread after the palette changed (the tray re-renders its icon).
    public static event Action? Changed;

    /// The one dictionary every window merges.
    public static ResourceDictionary Resources
    {
        get
        {
            if (shared is not null) return shared;
            shared = new ThemeDictionary();
            Apply(shared);
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            return shared;
        }
    }

    public static bool IsDark => !SystemParameters.HighContrast && ReadDword(PersonalizeKey, "AppsUseLightTheme", 1) == 0;

    /// The taskbar's own mode, which can differ from apps' (dark taskbar, light apps is Windows 11's default).
    public static bool TaskbarIsLight => ReadDword(PersonalizeKey, "SystemUsesLightTheme", 0) != 0;

    /// Merges the palette into `window` and keeps its title bar in step with dark mode.
    public static void Attach(Window window)
    {
        window.Resources.MergedDictionaries.Add(Resources);
        window.SourceInitialized += (_, _) => ApplyTitleBar(window);
        Changed += () => ApplyTitleBar(window);
    }

    public static void Detach()
    {
        if (shared is null) return;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Accessibility)) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || shared is null) return;
        dispatcher.BeginInvoke(() =>
        {
            Apply(shared);
            Changed?.Invoke();
        });
    }

    private static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        var dark = IsDark ? 1 : 0;
        // Windows 10 before 20H1 has no such attribute; the call fails harmlessly and the title bar stays light.
        _ = UiNative.DwmSetWindowAttribute(hwnd, UiNative.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private static void Apply(ResourceDictionary d)
    {
        if (SystemParameters.HighContrast)
        {
            ApplyHighContrast(d);
            return;
        }

        var accent = AccentColor();
        var dark = IsDark;
        Set(d, "Spit.Window", dark ? "#202020" : "#F3F3F3");
        Set(d, "Spit.Sidebar", dark ? "#1B1B1B" : "#EBEBEB");
        Set(d, "Spit.Card", dark ? "#2B2B2B" : "#FFFFFF");
        Set(d, "Spit.CardBorder", dark ? "#1FFFFFFF" : "#E5E5E5");
        Set(d, "Spit.Text", dark ? "#FFFFFF" : "#1A1A1A");
        Set(d, "Spit.TextSecondary", dark ? "#C8C8C8" : "#5D5D5D");
        Set(d, "Spit.TextTertiary", dark ? "#9A9A9A" : "#8A8A8A");
        Set(d, "Spit.Divider", dark ? "#3A3A3A" : "#E0E0E0");
        Set(d, "Spit.Control", dark ? "#2D2D2D" : "#FBFBFB");
        Set(d, "Spit.ControlBorder", dark ? "#454545" : "#D0D0D0");
        Set(d, "Spit.Hover", dark ? "#14FFFFFF" : "#0F000000");
        Set(d, "Spit.Selection", dark ? "#24FFFFFF" : "#1A000000");
        Set(d, "Spit.Notice", dark ? "#2A2A2A" : "#E9E9E9");
        Set(d, "Spit.Placeholder", dark ? "#3A3A3A" : "#E3E3E3");
        Set(d, "Spit.Danger", dark ? "#FF99A4" : "#C42B1C");
        Set(d, "Spit.Success", dark ? "#6CCB5F" : "#0F7B0F");
        Set(d, "Spit.Heat0", dark ? "#26FFFFFF" : "#26000000");
        Set(d, "Spit.BarBackground", dark ? "#F2262626" : "#F2F9F9F9");
        Set(d, "Spit.BarBorder", dark ? "#14FFFFFF" : "#1F000000");
        Set(d, "Spit.BarIdleFill", "#38808080");
        Set(d, "Spit.BarIdleBorder", dark ? "#38FFFFFF" : "#26000000");
        Set(d, "Spit.BarMicFill", "#2E808080");
        Set(d, "Spit.BarLevel", dark ? "#A0A0A0" : "#7A7A7A");
        Set(d, "Spit.Recording", "#E6FF3B30");
        Set(d, "Spit.RecordingLevel", "#D9FF3B30");

        // A dark window needs a lighter accent to stay readable as text and links.
        var shownAccent = dark ? Lighten(accent, 0.35) : accent;
        d["Spit.Accent"] = Frozen(shownAccent);
        d["Spit.AccentText"] = Frozen(dark ? Colors.Black : Colors.White);
        d["Spit.Heat1"] = Frozen(Color.FromArgb(0x59, shownAccent.R, shownAccent.G, shownAccent.B));
        d["Spit.Heat2"] = Frozen(Color.FromArgb(0xA6, shownAccent.R, shownAccent.G, shownAccent.B));
        d["Spit.Heat3"] = Frozen(shownAccent);
    }

    private static void ApplyHighContrast(ResourceDictionary d)
    {
        d["Spit.Window"] = SystemColors.WindowBrush;
        d["Spit.Sidebar"] = SystemColors.WindowBrush;
        d["Spit.Card"] = SystemColors.WindowBrush;
        d["Spit.CardBorder"] = SystemColors.WindowTextBrush;
        d["Spit.Text"] = SystemColors.WindowTextBrush;
        d["Spit.TextSecondary"] = SystemColors.WindowTextBrush;
        d["Spit.TextTertiary"] = SystemColors.GrayTextBrush;
        d["Spit.Divider"] = SystemColors.WindowTextBrush;
        d["Spit.Control"] = SystemColors.ControlBrush;
        d["Spit.ControlBorder"] = SystemColors.ControlTextBrush;
        d["Spit.Hover"] = SystemColors.HighlightBrush;
        d["Spit.Selection"] = SystemColors.HighlightBrush;
        d["Spit.Notice"] = SystemColors.WindowBrush;
        d["Spit.Placeholder"] = SystemColors.GrayTextBrush;
        d["Spit.Accent"] = SystemColors.HotTrackBrush;
        d["Spit.AccentText"] = SystemColors.HighlightTextBrush;
        d["Spit.Danger"] = SystemColors.WindowTextBrush;
        d["Spit.Success"] = SystemColors.WindowTextBrush;
        d["Spit.Heat0"] = SystemColors.GrayTextBrush;
        d["Spit.Heat1"] = SystemColors.HighlightBrush;
        d["Spit.Heat2"] = SystemColors.HighlightBrush;
        d["Spit.Heat3"] = SystemColors.WindowTextBrush;
        d["Spit.BarBackground"] = SystemColors.WindowBrush;
        d["Spit.BarBorder"] = SystemColors.WindowTextBrush;
        d["Spit.BarIdleFill"] = SystemColors.WindowBrush;
        d["Spit.BarIdleBorder"] = SystemColors.WindowTextBrush;
        d["Spit.BarMicFill"] = SystemColors.ControlBrush;
        d["Spit.BarLevel"] = SystemColors.WindowTextBrush;
        d["Spit.Recording"] = SystemColors.HighlightBrush;
        d["Spit.RecordingLevel"] = SystemColors.HighlightBrush;
    }

    /// `HKCU\…\DWM\AccentColor` is 0xAABBGGRR; Windows' default blue when it can't be read.
    private static Color AccentColor()
    {
        var raw = ReadDword(DwmKey, "AccentColor", unchecked((int)0xFFB85F00));
        var v = unchecked((uint)raw);
        return Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount),
        (byte)(c.G + (255 - c.G) * amount),
        (byte)(c.B + (255 - c.B) * amount));

    private static int ReadDword(string keyPath, string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return fallback;
        }
    }

    private static void Set(ResourceDictionary d, string key, string hex) =>
        d[key] = Frozen((Color)ColorConverter.ConvertFromString(hex));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

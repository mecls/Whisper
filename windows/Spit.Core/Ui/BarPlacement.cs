namespace Spit.Core;

/// A screen rectangle in physical pixels, Windows orientation (y grows downward, `Bottom` exclusive).
/// Its own type so the placement rule can be tested without WPF or Win32.
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// Where the bar sits: port of `HUDPanel.barOrigin` (prd-spit-mac-windows.md rule 38).
///
/// The two axes deliberately read different rectangles, exactly as on the Mac:
/// - **x** from the monitor's full bounds. "Centred" means centred on the display; a work area that
///   starts after a left-hand taskbar would put its midpoint half a taskbar to the right.
/// - **y** from the work area, which already excludes a bottom taskbar, so sitting `bottomGap` above its
///   bottom keeps the bar clear of it — or against the screen edge when the taskbar is on a side or
///   hidden.
///
/// Rounded (half away from zero, Swift's `rounded()`), because a half-pixel origin shimmers as the bar
/// resizes.
public static class BarPlacement
{
    /// The Mac's 10 pt gap above the Dock, as physical pixels.
    public const int DefaultBottomGap = 10;

    public static (int X, int Y) Compute(ScreenRect monitorBounds, ScreenRect workArea, int width, int height, int bottomGap = DefaultBottomGap)
    {
        var midX = monitorBounds.Left + monitorBounds.Width / 2.0;
        var x = (int)Math.Round(midX - width / 2.0, MidpointRounding.AwayFromZero);
        var y = workArea.Bottom - bottomGap - height;
        return (x, y);
    }
}

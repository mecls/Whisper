namespace Spit.Core.Tests;

/// Rule 38: x centred on the monitor's full bounds, y 10 px above the work area's bottom — the Windows form
/// of `HUDPanel.barOrigin`, whose bug was centring on the visible frame once the Dock moved to a side.
public sealed class BarPlacementTests
{
    private static readonly ScreenRect Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void CentresOnTheMonitorAndSitsAboveABottomTaskbar()
    {
        var work = new ScreenRect(0, 0, 1920, 1032);
        Assert.Equal((910, 1032 - 10 - 32), BarPlacement.Compute(Monitor, work, 100, 32));
    }

    [Fact]
    public void ALeftTaskbarDoesNotShiftTheCentre()
    {
        // Centring on the work area would land 24 px to the right.
        var work = new ScreenRect(48, 0, 1920, 1080);
        Assert.Equal((910, 1080 - 10 - 32), BarPlacement.Compute(Monitor, work, 100, 32));
    }

    [Fact]
    public void ARightTaskbarDoesNotShiftTheCentre()
    {
        var work = new ScreenRect(0, 0, 1872, 1080);
        Assert.Equal((910, 1038), BarPlacement.Compute(Monitor, work, 100, 32));
    }

    [Fact]
    public void ATopTaskbarLeavesTheBarAgainstTheBottomEdge()
    {
        var work = new ScreenRect(0, 48, 1920, 1080);
        Assert.Equal((910, 1038), BarPlacement.Compute(Monitor, work, 100, 32));
    }

    [Fact]
    public void ASecondaryMonitorWithANegativeOriginUsesItsOwnBounds()
    {
        // A 2560×1440 display to the left of and above the primary.
        var bounds = new ScreenRect(-2560, -360, 0, 1080);
        var work = new ScreenRect(-2560, -360, 0, 1032);
        Assert.Equal((-1280 - 22, 1032 - 10 - 12), BarPlacement.Compute(bounds, work, 44, 12));
    }

    [Fact]
    public void AnOddWidthRoundsHalfAwayFromZero()
    {
        // midX 960 - 45.5 = 914.5 → 915, as Swift's rounded() does.
        Assert.Equal(915, BarPlacement.Compute(Monitor, Monitor, 91, 20).X);
        // midX -1280 - 22.5 = -1302.5 → -1303.
        Assert.Equal(-1303, BarPlacement.Compute(new ScreenRect(-2560, 0, 0, 1440), new ScreenRect(-2560, 0, 0, 1440), 45, 20).X);
    }

    [Fact]
    public void TheGapIsConfigurable()
    {
        Assert.Equal(1080 - 20 - 12, BarPlacement.Compute(Monitor, Monitor, 44, 12, bottomGap: 20).Y);
    }
}

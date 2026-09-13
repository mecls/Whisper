using System.Diagnostics;

namespace Spit.App.Tests;

/// Rule 30 is about input Windows drops, which happens only into a process above the sender. A process at Spit's
/// own level is never an "admin window", whatever its elevation flag says.
public sealed class ElevationRouteTests
{
    [WindowsFact]
    public void SpitsOwnProcessNeverBlocksItsInput()
    {
        Assert.False(ElevationProbe.BlocksInputFromSpit(Environment.ProcessId));
    }

    [WindowsFact]
    public void AChildStartedBySpitRunsAtItsLevelAndDoesNotBlockItsInput()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true });
        Assert.NotNull(child);
        try
        {
            Assert.False(ElevationProbe.BlocksInputFromSpit(child.Id));
            Assert.Equal(ElevationProbe.IsCurrentProcessElevated(), ElevationProbe.IsElevated(child.Id));
        }
        finally
        {
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }
}

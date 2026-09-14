using System.Diagnostics;

namespace Spit.App.Tests;

/// Rule 30 is about input Windows drops, which happens only into a process at a higher integrity level than Spit.
public sealed class ElevationRouteTests
{
    [WindowsFact]
    public void SpitsOwnProcessNeverBlocksItsInput()
    {
        Assert.False(ElevationProbe.BlocksInputFromSpit(Environment.ProcessId));
    }

    [WindowsFact]
    public void SpitsOwnIntegrityLevelIsReadable()
    {
        var level = ElevationProbe.IntegrityOf(Environment.ProcessId, out var denied);

        Assert.False(denied);
        Assert.NotNull(level);
        Assert.InRange(level.Value, 0x2000, 0x4000);   // Medium, High (elevated) or System
    }

    [WindowsFact]
    public void AChildStartedBySpitRunsAtItsLevelAndDoesNotBlockItsInput()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true });
        Assert.NotNull(child);
        try
        {
            Assert.False(ElevationProbe.BlocksInputFromSpit(child.Id));
            Assert.Equal(ElevationProbe.IntegrityOf(Environment.ProcessId, out _), ElevationProbe.IntegrityOf(child.Id, out _));
        }
        finally
        {
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }
}

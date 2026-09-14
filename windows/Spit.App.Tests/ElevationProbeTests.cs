namespace Spit.App.Tests;

/// Rule 30: the probe answers for any process id without throwing.
public sealed class ElevationProbeTests
{
    [WindowsFact]
    public void CurrentProcess_AnswersTheSameBothWays()
    {
        var elevated = ElevationProbe.IsCurrentProcessElevated();

        Assert.Equal(elevated, ElevationProbe.IsElevated(Environment.ProcessId));
    }

    [WindowsFact]
    public void ProcessThatDoesNotExist_IsNotElevated()
    {
        // Not access denied, so it doesn't count as elevated: there is no window to lose a paste into.
        Assert.False(ElevationProbe.IsElevated(0x7FFFFFFC));
    }
}

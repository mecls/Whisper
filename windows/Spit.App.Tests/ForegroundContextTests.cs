using System.Diagnostics;
using System.IO;

namespace Spit.App.Tests;

/// Rule 31: identity is the lowercased executable name and its FileDescription.
public sealed class ForegroundContextTests
{
    [WindowsFact]
    public void Current_ReturnsAnAppOrNullWithoutThrowing()
    {
        var app = ForegroundContext.Current();
        _ = ForegroundContext.ForegroundProcessId();

        if (app is not null)
        {
            Assert.NotNull(app.BundleId);
            Assert.Equal(app.BundleId.ToLowerInvariant(), app.BundleId);
            Assert.False(string.IsNullOrWhiteSpace(app.Name));
        }
    }

    [WindowsFact]
    public void ForProcess_OfTheTestHost_UsesItsLowercasedExecutableName()
    {
        var app = ForegroundContext.ForProcess(Environment.ProcessId);

        Assert.NotNull(app);
        Assert.Equal(Path.GetFileName(Environment.ProcessPath!).ToLowerInvariant(), app.BundleId);
        Assert.False(string.IsNullOrWhiteSpace(app.Name));
    }

    [WindowsFact]
    public void ForProcess_OfASpawnedProcess_UsesItsFileDescription()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        Assert.NotNull(process);
        try
        {
            var app = ForegroundContext.ForProcess(process.Id);

            Assert.NotNull(app);
            Assert.Equal("cmd.exe", app.BundleId);
            // cmd.exe carries a FileDescription, so the name is not the bare file name fallback.
            Assert.NotEqual("cmd", app.Name);
            Assert.False(string.IsNullOrWhiteSpace(app.Name));
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }
}

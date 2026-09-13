using Microsoft.Win32;

namespace Spit.App.Tests;

/// Rule 44: the Run value round-trips under `SpitTest-<guid>`, never the real `Spit` value.
public sealed class LaunchAtLoginTests
{
    [WindowsFact]
    public void EnableReadDisable_UnderATestValueName()
    {
        var name = "SpitTest-" + Guid.NewGuid().ToString("N");
        const string exe = @"C:\Users\friend\AppData\Local\Spit\Spit.exe";
        var login = new LaunchAtLogin(name, exe);
        try
        {
            Assert.False(login.IsEnabled());

            login.SetEnabled(true);
            Assert.True(login.IsEnabled());
            using (var key = Registry.CurrentUser.OpenSubKey(LaunchAtLogin.RunKeyPath))
            {
                Assert.NotNull(key);
                Assert.Equal($"\"{exe}\"", key.GetValue(name));
            }

            login.SetEnabled(false);
            Assert.False(login.IsEnabled());
            login.SetEnabled(false);   // disabling twice is not an error
        }
        finally
        {
            using var key = Registry.CurrentUser.OpenSubKey(LaunchAtLogin.RunKeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    [WindowsFact]
    public void InstalledByVelopack_PointsAtTheStubThatSurvivesUpdates()
    {
        Assert.Equal(
            @"C:\Users\friend\AppData\Local\Spit\Spit.exe",
            LaunchAtLogin.ResolveExecutablePath(@"C:\Users\friend\AppData\Local\Spit\current\Spit.exe"));
    }

    [WindowsFact]
    public void NotInstalled_PointsAtTheRunningExe()
    {
        const string exe = @"C:\src\spit\windows\Spit.App\bin\Release\Spit.exe";

        Assert.Equal(exe, LaunchAtLogin.ResolveExecutablePath(exe));
    }
}

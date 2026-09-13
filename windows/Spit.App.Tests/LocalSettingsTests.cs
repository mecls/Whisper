using System.IO;

namespace Spit.App.Tests;

/// The settings refine and sync read and write go through `settings.json`, so a synced Mode survives a relaunch.
public sealed class LocalSettingsTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    [WindowsFact]
    public void ModeLanguageAndHotkey_RoundTripThroughSettingsJson()
    {
        var paths = new AppPaths(temp.Path);
        var local = new LocalSettings(new SettingsStore(paths));
        Assert.Equal("clean", local.Mode);
        Assert.Equal("auto", local.Language);
        Assert.Equal("rightCtrl", local.Hotkey);

        local.Mode = "literal";
        local.Language = "pt";
        local.Hotkey = "rightAlt";

        var reloaded = new LocalSettings(new SettingsStore(paths));
        Assert.Equal("literal", reloaded.Mode);
        Assert.Equal("pt", reloaded.Language);
        Assert.Equal("rightAlt", reloaded.Hotkey);
        Assert.True(File.Exists(paths.SettingsFile));
    }

    [WindowsFact]
    public void AMacHotkey_ReadsAsTheWindowsDefault()
    {
        var local = new LocalSettings(new SettingsStore(new AppPaths(temp.Path)))
        {
            Hotkey = "rightCommand",
        };

        Assert.Equal("rightCtrl", local.Hotkey);
    }
}

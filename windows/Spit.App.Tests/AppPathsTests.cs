using System.IO;

namespace Spit.App.Tests;

/// Rule 5: everything Spit writes lives under one root outside the install directory.
public sealed class AppPathsTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    [WindowsFact]
    public void OverriddenRoot_HoldsEveryFileAndCreatesFoldersOnDemand()
    {
        var paths = new AppPaths(temp.Path);
        Assert.False(Directory.Exists(temp.Path));

        Assert.Equal(Path.Combine(temp.Path, "models"), paths.Models);
        Assert.True(Directory.Exists(paths.Models));
        Assert.Equal(Path.Combine(temp.Path, "logs"), paths.Logs);
        Assert.True(Directory.Exists(paths.Logs));
        Assert.Equal(Path.Combine(temp.Path, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(temp.Path, "dictionary.json"), paths.DictionaryFile);
        Assert.Equal(Path.Combine(temp.Path, "insights-cache.json"), paths.InsightsCacheFile);
    }

    [WindowsFact]
    public void DataDirVariable_ReplacesTheRootOnlyWhenSet()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Miraside", "Spit");

        Assert.Equal("SPIT_DATA_DIR", AppPaths.DataDirVariable);
        Assert.Equal(Path.GetFullPath(temp.Path), AppPaths.ResolveRoot(temp.Path));
        Assert.Equal(real, AppPaths.ResolveRoot(null));
        Assert.Equal(real, AppPaths.ResolveRoot("  "));
    }

    [WindowsFact]
    public void Default_IsMirasideSpitUnderLocalAppData()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Miraside", "Spit");

        Assert.Equal(expected, AppPaths.Default.Root);
    }
}

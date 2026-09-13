using System.IO;

namespace Spit.App.Tests;

/// Rule 25: `dictionary.json` holds terms, survives a relaunch, and a bad file costs only the cache.
public sealed class DictionaryCacheTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    [WindowsFact]
    public void Update_IsReadBackByTheNextLaunch()
    {
        var paths = new AppPaths(temp.Path);

        new DictionaryCache(paths).Update(["Miraside", "Spit", "WhisperKit"]);

        Assert.Equal(["Miraside", "Spit", "WhisperKit"], new DictionaryCache(paths).Terms);
    }

    [WindowsFact]
    public void CorruptFile_GivesNoTerms()
    {
        var paths = new AppPaths(temp.Path);
        File.WriteAllText(paths.DictionaryFile, "not json");

        Assert.Empty(new DictionaryCache(paths).Terms);
    }
}

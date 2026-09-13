using System.IO;

namespace Spit.App;

/// Everything Spit writes lives under `%LOCALAPPDATA%\Miraside\Spit\`, never in Velopack's install
/// directory: an update replaces that directory and an uninstall deletes it, and neither may take a
/// 574 MB model or the user's settings with it (rule 5).
public sealed class AppPaths
{
    private static readonly Lazy<AppPaths> DefaultPaths = new(() => new AppPaths(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Miraside", "Spit")));

    /// The real data folder.
    public static AppPaths Default => DefaultPaths.Value;

    /// `root` is overridable so tests never touch the real data folder.
    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    // Folders are created when first asked for, so a fresh install needs no set-up step and a
    // folder the user deleted while Spit was running comes back on the next write.
    public string Models => Ensure(Path.Combine(Root, "models"));

    public string Logs => Ensure(Path.Combine(Root, "logs"));

    public string SettingsFile => Path.Combine(Ensure(Root), "settings.json");

    public string DictionaryFile => Path.Combine(Ensure(Root), "dictionary.json");

    public string InsightsCacheFile => Path.Combine(Ensure(Root), "insights-cache.json");

    private static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }
}

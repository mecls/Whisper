using System.IO;

namespace Spit.App;

/// Everything Spit writes lives under `%LOCALAPPDATA%\Miraside\Spit\`, never in Velopack's install
/// directory: an update replaces that directory and an uninstall deletes it, and neither may take a
/// 574 MB model or the user's settings with it (rule 5).
public sealed class AppPaths
{
    /// When set, replaces the data folder for the whole process. Only CI's `--smoke-test` step uses it, so the
    /// runner's copy of the model is read from a temp folder instead of the real `%LOCALAPPDATA%`.
    public const string DataDirVariable = "SPIT_DATA_DIR";

    private static readonly Lazy<AppPaths> DefaultPaths = new(() => new AppPaths(ResolveRoot(Environment.GetEnvironmentVariable(DataDirVariable))));

    /// The real data folder, or `SPIT_DATA_DIR` when that is set.
    public static AppPaths Default => DefaultPaths.Value;

    /// The root `Default` uses for a given value of `SPIT_DATA_DIR`; blank means unset.
    public static string ResolveRoot(string? dataDirVariable) =>
        string.IsNullOrWhiteSpace(dataDirVariable)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Miraside", "Spit")
            : Path.GetFullPath(dataDirVariable);

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

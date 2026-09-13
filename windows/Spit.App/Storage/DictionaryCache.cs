using System.IO;
using System.Text.Json;

namespace Spit.App;

/// Terms fed to Whisper's prompt, cached in `dictionary.json` so the first dictation after launch
/// already has them. Terms only, never a transcript (rule 25).
public sealed class DictionaryCache
{
    private readonly Lock gate = new();
    private readonly string path;
    private IReadOnlyList<string> terms;

    public DictionaryCache(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        path = paths.DictionaryFile;
        terms = Load(path);
    }

    public IReadOnlyList<string> Terms
    {
        get { lock (gate) return terms; }
    }

    /// Replaces the terms; a failed write is logged and the new terms still apply for this launch.
    public void Update(IEnumerable<string> newTerms)
    {
        ArgumentNullException.ThrowIfNull(newTerms);
        var copy = newTerms.ToArray();
        lock (gate)
        {
            terms = copy;
            try
            {
                AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(copy));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Failure("dictionary", "write dictionary.json", e);
            }
        }
    }

    private static string[] Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<string?[]>(File.ReadAllBytes(path))?.OfType<string>().ToArray() ?? [];
        }
        catch (JsonException e)
        {
            Log.Failure("dictionary", "parse dictionary.json", e);
            return [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Failure("dictionary", "read dictionary.json", e);
            return [];
        }
    }
}

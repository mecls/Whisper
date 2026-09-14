using System.Text.Json;

namespace Spit.Core;

/// The last successful `GET /v1/insights` response, on disk. Port of
/// mac/Voice/Insights/InsightsCache.swift.
///
/// Its whole job is that the window has something to draw the instant it opens, including offline.
/// Writing it is only safe because the response contains no transcript text (see `Insights`, rule 25);
/// if that ever stops being true, this file has to go, not gain a filter.
public sealed class InsightsCache(string filePath)
{
    /// Lives in `%LOCALAPPDATA%\Miraside\Spit\` — the app owns that path (rule 44).
    public const string FileName = "insights-cache.json";

    public string FilePath { get; } = filePath;

    /// A missing, unreadable or corrupt file is "no cache", never an error the user sees. The window then
    /// shows its loading state and refreshes — one refresh is the entire cost of a bad file.
    public Insights? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var insights = JsonSerializer.Deserialize<Insights>(File.ReadAllBytes(FilePath), SpitJson.Options);
            // Valid JSON with a key missing parses to nulls here, where Swift's decoder would throw.
            return insights is { Totals: not null, Streak: not null, Days: not null, Apps: not null } ? insights : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// Atomic: a temp file beside the target, then replace.
    public void Save(Insights insights)
    {
        var temp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (Path.GetDirectoryName(FilePath) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(insights, SpitJson.Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // A cache that cannot be written costs one refresh next time. Never worth failing, or telling
            // the user about, an operation they did not ask for.
            try { File.Delete(temp); } catch (Exception) { /* best effort */ }
        }
    }
}

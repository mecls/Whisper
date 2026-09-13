using System.IO;

namespace Spit.App;

/// Temp file, flush, then rename over the target: a crash or power cut mid-write leaves the old file or
/// the new one, never half of each (build spec §7).
internal static class AtomicFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Failure("storage", "delete temp file", e);
            }
            throw;
        }
    }
}

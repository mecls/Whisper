using System.Buffers;
using System.Security.Cryptography;

namespace Spit.Core;

/// Downloads a catalog model to `<file>.partial`, hashing while it streams, and renames it to its final
/// name only when the SHA-256 matches the pin. Only the final name at the exact pinned size counts as
/// downloaded, so an interrupted or tampered download is never loaded: the Windows form of
/// `ModelManager.requiredEntries` (rule 41, build spec invariant 8).
public sealed class ModelDownloader : IDisposable
{
    public const string PartialSuffix = ".partial";

    private const int BufferBytes = 1 << 20;
    private const double ProgressStep = 0.001;

    private readonly HttpClient http;

    /// `handler` and `catalog` are injectable so tests serve a small file against a test-only pin.
    public ModelDownloader(string modelsDirectory, ModelCatalog? catalog = null, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        ModelsDirectory = modelsDirectory;
        Catalog = catalog ?? ModelCatalog.Default;
        // 30 s to connect and no cap on the whole transfer: 1.6 GB on a slow line takes what it takes.
        http = handler is null
            ? new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public string ModelsDirectory { get; }

    public ModelCatalog Catalog { get; }

    public string PathFor(string file) => Path.Combine(ModelsDirectory, Require(file).File);

    public string PartialPathFor(string file) => PathFor(file) + PartialSuffix;

    public bool IsDownloaded(string file)
    {
        var entry = Catalog.Find(file);
        if (entry is null) return false;
        var info = new FileInfo(Path.Combine(ModelsDirectory, entry.File));
        return info.Exists && info.Length == entry.Bytes;
    }

    /// Removes the model and any leftover `.partial`. Throws if the file is in use, for the UI to report.
    public void Delete(string file)
    {
        File.Delete(PathFor(file));
        File.Delete(PartialPathFor(file));
    }

    /// Returns the final path. Progress runs 0…1. A hash mismatch throws `InvalidDataException` with
    /// `Strings.ModelChecksumMismatch`; any failure leaves no `.partial`, so the next attempt restarts
    /// from zero.
    public async Task<string> DownloadAsync(string file, Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var entry = Require(file);
        Directory.CreateDirectory(ModelsDirectory);
        var partial = PartialPathFor(file);
        var final = PathFor(file);
        try
        {
            string actual;
            using (var response = await http.GetAsync(Catalog.UrlFor(entry), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous);
                actual = await CopyHashingAsync(source, target, entry.Bytes, progress, cancellationToken).ConfigureAwait(false);
            }

            if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException(Strings.ModelChecksumMismatch);
            }

            File.Move(partial, final, overwrite: true);
            progress?.Invoke(1);
            return final;
        }
        catch
        {
            DeleteQuietly(partial);
            throw;
        }
    }

    public void Dispose() => http.Dispose();

    private ModelCatalog.Entry Require(string file) =>
        Catalog.Find(file) ?? throw new ArgumentException($"'{file}' is not a model in the catalog.", nameof(file));

    private static async Task<string> CopyHashingAsync(Stream source, Stream target, long expectedBytes, Action<double>? progress, CancellationToken cancellationToken)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            long total = 0;
            var reported = -1.0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
                var fraction = Math.Min(1, (double)total / Math.Max(1, expectedBytes));
                if (fraction - reported >= ProgressStep)
                {
                    progress?.Invoke(fraction);
                    reported = fraction;
                }
            }
            return Convert.ToHexStringLower(sha.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The download's own failure is the one to surface; a leftover .partial is never loaded
            // and is overwritten by the next attempt.
        }
    }
}

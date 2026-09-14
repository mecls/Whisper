using System.Net;
using System.Security.Cryptography;

namespace Spit.Core.Tests;

/// Build spec invariant 8 and AC-7: only a download whose SHA-256 matches the pin becomes a model file.
/// Not a port of a Mac test class (WhisperKit verified its own downloads); extra to the parity set.
public sealed class ModelDownloaderTests : IDisposable
{
    private const string TestFile = "ggml-test.bin";
    private static readonly Uri TestBase = new("https://models.test/");
    private static readonly byte[] Payload = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31 % 251)).ToArray();

    private readonly string directory = Path.Combine(Path.GetTempPath(), "spit-model-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task MatchingHash_RenamesThePartialToTheFinalName()
    {
        var handler = new FixedResponseHandler(HttpStatusCode.OK, Payload);
        using var downloader = new ModelDownloader(directory, Pinning(Sha256(Payload), Payload.Length), handler);
        var progress = new List<double>();

        var path = await downloader.DownloadAsync(TestFile, progress.Add, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(directory, TestFile), path);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path + ModelDownloader.PartialSuffix));
        Assert.True(downloader.IsDownloaded(TestFile));
        Assert.Equal(1.0, progress[^1]);
        Assert.Equal(new Uri(TestBase, TestFile), Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task MismatchingHash_LeavesNoFinalFileAndNoPartialAndReportsChecksumMismatch()
    {
        var handler = new FixedResponseHandler(HttpStatusCode.OK, Payload);
        using var downloader = new ModelDownloader(directory, Pinning(Sha256([1, 2, 3]), Payload.Length), handler);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => downloader.DownloadAsync(TestFile, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(Strings.ModelChecksumMismatch, error.Message);
        Assert.Equal("Model download failed — checksum mismatch", error.Message);
        Assert.False(File.Exists(Path.Combine(directory, TestFile)));
        Assert.False(File.Exists(Path.Combine(directory, TestFile + ModelDownloader.PartialSuffix)));
        Assert.False(downloader.IsDownloaded(TestFile));
    }

    [Fact]
    public void PartialFile_IsNeverReportedAsDownloaded()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, TestFile + ModelDownloader.PartialSuffix), Payload);
        using var downloader = new ModelDownloader(directory, Pinning(Sha256(Payload), Payload.Length), new FixedResponseHandler(HttpStatusCode.OK, Payload));

        Assert.False(downloader.IsDownloaded(TestFile));
    }

    [Fact]
    public void FinalFileOfTheWrongSize_IsNotDownloaded()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, TestFile), Payload[..^1]);
        using var downloader = new ModelDownloader(directory, Pinning(Sha256(Payload), Payload.Length), new FixedResponseHandler(HttpStatusCode.OK, Payload));

        Assert.False(downloader.IsDownloaded(TestFile));
    }

    [Fact]
    public async Task FailedRequest_LeavesNoPartial()
    {
        using var downloader = new ModelDownloader(directory, Pinning(Sha256(Payload), Payload.Length), new FixedResponseHandler(HttpStatusCode.NotFound, []));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => downloader.DownloadAsync(TestFile, cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Path.Combine(directory, TestFile + ModelDownloader.PartialSuffix)));
        Assert.False(File.Exists(Path.Combine(directory, TestFile)));
    }

    [Fact]
    public void DefaultCatalog_PinsEveryModelFromHuggingFace()
    {
        var catalog = ModelCatalog.Default;

        Assert.Equal(
            [
                new ModelCatalog.Entry("ggml-small-q8_0.bin", Strings.ModelLabelSmall, 264_464_607, "49c8fb02b65e6049d5fa6c04f81f53b867b5ec9540406812c643f177317f779f"),
                new ModelCatalog.Entry("ggml-large-v3-turbo-q5_0.bin", Strings.ModelLabelTurbo, 574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
                new ModelCatalog.Entry("ggml-large-v3-turbo.bin", Strings.ModelLabelLarge, 1_624_555_275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"),
            ],
            catalog.Entries);
        Assert.Equal(
            new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q8_0.bin"),
            catalog.UrlFor(catalog.Find(ModelCatalog.DefaultFile)!));
        Assert.Equal("whisper.cpp/ggml-small-q8_0", ModelCatalog.AsrModelFor(ModelCatalog.DefaultFile));
    }

    private static ModelCatalog Pinning(string sha256, long bytes) =>
        new([new ModelCatalog.Entry(TestFile, "Test model", bytes, sha256)], TestBase);

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class FixedResponseHandler(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        public List<Uri?> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }
}

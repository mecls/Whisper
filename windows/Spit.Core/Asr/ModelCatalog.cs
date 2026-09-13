namespace Spit.Core;

/// The speech models, in whisper.cpp's ggml form (rule 41). Sizes and SHA-256s are pinned in code: a download is
/// accepted only if it hashes to exactly these bytes (build spec invariant 8). Hashes are Hugging Face's LFS oids,
/// read 2026-09-13.
///
/// Whisper small is the default and the recommendation on Windows (Miguel, open question 3): large-v3-turbo took
/// 53–85 s to transcribe 12.5 s of audio on a GPU-less 4-vCPU CI runner, and friends' PCs mostly have no GPU Vulkan
/// can use. The Mac keeps large-v3-turbo, which runs on its Neural Engine. The larger models stay available for PCs
/// fast enough to run them.
public sealed class ModelCatalog
{
    public sealed record Entry(string File, string Label, long Bytes, string Sha256);

    /// q8_0: half the size of the full-precision small model with no audible loss in accuracy.
    public const string SmallFile = "ggml-small-q8_0.bin";
    public const string TurboCompressedFile = "ggml-large-v3-turbo-q5_0.bin";
    public const string TurboFullFile = "ggml-large-v3-turbo.bin";
    public const string DefaultFile = SmallFile;

    public static readonly Uri HuggingFaceBase = new("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/");

    public static ModelCatalog Default { get; } = new(
        [
            new Entry(SmallFile, Strings.ModelLabelSmall, 264_464_607, "49c8fb02b65e6049d5fa6c04f81f53b867b5ec9540406812c643f177317f779f"),
            new Entry(TurboCompressedFile, Strings.ModelLabelTurbo, 574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
            new Entry(TurboFullFile, Strings.ModelLabelLarge, 1_624_555_275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"),
        ],
        HuggingFaceBase);

    /// Tests pass their own entries, with a test-only pin, and a base URL their fake handler answers.
    public ModelCatalog(IReadOnlyList<Entry> entries, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.AbsoluteUri.EndsWith('/')) throw new ArgumentException("The base URL must end in '/'.", nameof(baseUri));
        Entries = entries;
        BaseUri = baseUri;
    }

    public IReadOnlyList<Entry> Entries { get; }

    public Uri BaseUri { get; }

    public Entry? Find(string file) => Entries.FirstOrDefault(e => string.Equals(e.File, file, StringComparison.Ordinal));

    public Uri UrlFor(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new Uri(BaseUri, entry.File);
    }

    /// `asrModel` as reported: `whisper.cpp/<file without .bin>`, so Windows dictations form their own
    /// population when latency is read back (rule 41).
    public static string AsrModelFor(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        return "whisper.cpp/" + (file.EndsWith(".bin", StringComparison.Ordinal) ? file[..^4] : file);
    }
}

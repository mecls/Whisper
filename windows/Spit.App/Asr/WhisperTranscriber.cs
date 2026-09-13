using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Spit.Core;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Spit.App;

/// On-device Whisper through Whisper.net (rule 41), with WhisperKitTranscriber's decode rules:
/// temperature 0 and no fallback to higher temperatures, the language hint or detection, dictionary
/// terms as the prompt with one retry without it, and a 1 s warm-up after load. whisper.cpp can't run
/// two decodes on one model instance, so every inference — one-pass, stream pass or tail — waits its
/// turn on one lock (rule 42).
public sealed partial class WhisperTranscriber : ISegmentTranscriber, IAsyncDisposable
{
    public const int SampleRate = 16_000;

    /// The Mac keeps the first 150 prompt tokens. Whisper.net exposes no tokenizer, so this counts words;
    /// dictionary terms are mostly names, which run to about 1.5 tokens a word.
    public const int PromptWordLimit = 100;

    private readonly SemaphoreSlim inference = new(1, 1);
    private readonly ModelDownloader models;
    private volatile WhisperFactory? factory;

    public WhisperTranscriber(ModelDownloader models, string modelFile)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFile);
        this.models = models;
        ModelFile = modelFile;
    }

    public string ModelFile { get; }

    /// `whisper.cpp/<file without .bin>` (rule 41).
    public string AsrModel => ModelCatalog.AsrModelFor(ModelFile);

    public bool IsReady => factory is not null;

    /// Loads the model; downloading is `ModelDownloader`'s job. Only a verified file with its final name
    /// is opened, never a `.partial` (build spec invariant 8).
    public async Task PrepareAsync(Action<double> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!models.IsDownloaded(ModelFile)) throw new FileNotFoundException(Strings.ModelNotDownloaded, ModelFile);

        await inference.WaitAsync();
        try
        {
            if (factory is null)
            {
                progress(0.1);
                ConfigureRuntimes();
                var path = models.PathFor(ModelFile);
                var loaded = await Task.Run(() => WhisperFactory.FromPath(path));
                Log.Info("asr", $"loaded {AsrModel} with the {RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown"} runtime");
                progress(0.9);
                // The first real transcription after load is markedly slower (docs/SPIKES.md): warm it
                // with 1 s of silence, as the Mac does.
                await WarmUpAsync(loaded);
                factory = loaded;
            }
            progress(1);
        }
        finally
        {
            inference.Release();
        }
    }

    public async Task<Transcript> TranscribeAsync(float[] samples, TranscribeHint hint, Action<double>? progress)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(hint);

        await inference.WaitAsync();
        try
        {
            var loaded = factory ?? throw new InvalidOperationException("The speech model is not loaded.");
            var started = Stopwatch.GetTimestamp();
            var language = LanguageOrDetect(hint.Language);
            var prompt = PromptFor(hint.Vocabulary);

            var (segments, detected) = await DecodeAsync(loaded, samples, language, prompt, progress, CancellationToken.None);
            var retried = false;
            if (segments.Count == 0 && prompt is not null)
            {
                // Mac rule: with one temperature-0 attempt and nothing to fall back to, a vocabulary
                // prompt can come back empty on real speech. Retry once without it rather than show
                // "Nothing heard". No progress on the retry, so the bar never jumps backwards.
                (segments, detected) = await DecodeAsync(loaded, samples, language, prompt: null, progress: null, CancellationToken.None);
                retried = true;
            }

            var text = TextOf(segments);
            var ms = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Log.Info("asr", $"{samples.Length} samples to {text.Length} chars in {ms} ms{(retried ? ", retried without prompt" : "")}");
            return new Transcript(text, detected ?? language ?? "unknown", ms);
        }
        finally
        {
            inference.Release();
        }
    }

    /// One stream pass: the same decode, with Whisper's segment times kept, relative to `samples`. The
    /// streaming session logs each pass, so this does not.
    public async Task<IReadOnlyList<StreamSegment>> TranscribeSegmentsAsync(float[] samples, TranscribeHint hint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(hint);

        await inference.WaitAsync(ct);
        try
        {
            var loaded = factory ?? throw new InvalidOperationException("The speech model is not loaded.");
            var language = LanguageOrDetect(hint.Language);
            var prompt = PromptFor(hint.Vocabulary);
            var (segments, _) = await DecodeAsync(loaded, samples, language, prompt, progress: null, ct);
            if (segments.Count == 0 && prompt is not null)
            {
                (segments, _) = await DecodeAsync(loaded, samples, language, prompt: null, progress: null, ct);
            }
            return segments;
        }
        finally
        {
            inference.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await inference.WaitAsync();
        try
        {
            factory?.Dispose();
            factory = null;
        }
        finally
        {
            inference.Release();
        }
    }

    /// Dictionary terms joined as Whisper's initial prompt, cut to `PromptWordLimit` words; null when there
    /// are none. The leading space matches how the Mac encodes its prompt.
    internal static string? PromptFor(IReadOnlyList<string> vocabulary)
    {
        if (vocabulary.Count == 0) return null;
        var words = string.Join(", ", vocabulary).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length == 0 ? null : " " + string.Join(' ', words.Take(PromptWordLimit));
    }

    // CUDA is left out (rule 24): it needs a CUDA toolkit friends won't have. Whisper.net stops at the
    // first runtime that loads, and reads this order only before its first factory exists.
    private static void ConfigureRuntimes() =>
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];

    private static string? LanguageOrDetect(string? language) =>
        string.IsNullOrWhiteSpace(language) || language == "auto" ? null : language;

    private static WhisperProcessor Build(WhisperFactory loaded, string? language, string? prompt, Action<double>? progress)
    {
        var builder = loaded.CreateBuilder()
            .WithTemperature(0)
            .WithTemperatureInc(0);   // no retries at higher temperatures (Mac temperatureFallbackCount: 0)
        builder = language is null ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        if (prompt is not null) builder = builder.WithPrompt(prompt);
        if (progress is not null) builder = builder.WithProgressHandler(percent => progress(Math.Clamp(percent / 100.0, 0, 1)));
        return builder.Build();
    }

    private static async Task<(List<StreamSegment> Segments, string? Language)> DecodeAsync(
        WhisperFactory loaded, float[] samples, string? language, string? prompt, Action<double>? progress, CancellationToken ct)
    {
        await using var processor = Build(loaded, language, prompt, progress);
        var segments = new List<StreamSegment>();
        string? detected = null;
        await foreach (var segment in processor.ProcessAsync(samples, ct))
        {
            if (detected is null && !string.IsNullOrEmpty(segment.Language)) detected = segment.Language;
            if (string.IsNullOrWhiteSpace(segment.Text) || NonSpeechTag().IsMatch(segment.Text)) continue;
            segments.Add(new StreamSegment((float)segment.Start.TotalSeconds, (float)segment.End.TotalSeconds, segment.Text));
        }
        return (segments, detected);
    }

    /// Whisper carries its own leading spaces, so segments are concatenated, not joined.
    private static string TextOf(IEnumerable<StreamSegment> segments) => string.Concat(segments.Select(s => s.Text)).Trim();

    private static async Task WarmUpAsync(WhisperFactory loaded)
    {
        try
        {
            await DecodeAsync(loaded, new float[SampleRate], "en", prompt: null, progress: null, CancellationToken.None);
        }
        catch (Exception e)
        {
            // A failed warm-up only costs the first dictation some speed (the Mac ignores it with `try?`).
            Log.Failure("asr", "warm-up", e);
        }
    }

    // whisper.cpp writes silence and noise as bracketed tags such as `[BLANK_AUDIO]`, which WhisperKit
    // drops as special tokens; they must not reach the paste as words or defeat "Nothing heard".
    [GeneratedRegex(@"^\s*\[[A-Z_ ]+\]\s*$")]
    private static partial Regex NonSpeechTag();
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NAudio.Dsp;
using NAudio.Wave;
using Spit.Core;
using Whisper.net.LibraryLoader;

namespace Spit.App;

/// <summary>
/// `Spit.exe --smoke-test &lt;wav&gt; --report &lt;json&gt;`: CI's proof that the published app transcribes on Windows
/// (build spec AC-6). No UI, no hook, no single-instance lock, no network; the model must already be in the
/// data folder (`SPIT_DATA_DIR` in CI) — nothing is downloaded.
///
/// It runs the one-pass transcription the app falls back to, then the live path the way a dictation drives it:
/// a `StreamingSession` fed 100 ms at a time in real time, finished, with the tail pass and the stitch. The
/// report holds the fixture's transcript, which is test audio, never anyone's voice.
/// </summary>
public static class SmokeTest
{
    public const string Flag = "--smoke-test";
    public const string ReportFlag = "--report";

    /// Malformed arguments: no report can be written, because there is nowhere to write it.
    public const int UsageExitCode = 2;

    /// en.wav's exact transcript is not in the repo. `WhisperKitTranscriberTests` biases the fixture with
    /// "Miraside" and "Convex", and docs/SPIKES.md records Whisper hearing "Miraside" as "Miracyte" and
    /// "Ollama key" as "Olamaki" without that bias — so these are stems that survive those misspellings.
    internal static readonly string[] ExpectedWordStems = ["mira", "convex", "ollama", "olama"];

    /// The Mac test's hint, so both clients are asked the same question.
    internal static readonly string[] Vocabulary = ["Miraside", "Convex"];

    private const int ChunkMs = 100;
    private const int ResampleBlockFrames = 4096;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// The WAV and report paths, or null when either is missing.
    public static (string Wav, string Report)? Parse(IReadOnlyList<string> args)
    {
        var wav = ValueAfter(args, Flag);
        var report = ValueAfter(args, ReportFlag);
        return wav is null || report is null ? null : (wav, report);
    }

    /// Exit 0 only when the one-pass text is non-empty and contains one of the fixture's words; 1 otherwise,
    /// including any exception (reported with its type and message).
    public static int Run(string wavPath, string reportPath) => RunAsync(wavPath, reportPath).GetAwaiter().GetResult();

    /// The file as 16 kHz mono Float32, converted the way `AudioCapture` converts a microphone: NAudio decodes,
    /// channels are averaged, and WDL resamples in input-driven mode.
    internal static float[] LoadSamples(string path)
    {
        using var reader = new WaveFileReader(path);
        var source = reader.ToSampleProvider();
        var channels = Math.Max(1, source.WaveFormat.Channels);
        var rate = source.WaveFormat.SampleRate;

        var mono = new List<float>();
        var buffer = new float[channels * ResampleBlockFrames];
        int read;
        while ((read = source.Read(buffer.AsSpan())) > 0)
        {
            for (var frame = 0; frame + channels <= read; frame += channels)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++) sum += buffer[frame + c];
                mono.Add(sum / channels);
            }
        }
        return rate == AudioCapture.SampleRate ? mono.ToArray() : Resample(mono.ToArray(), rate);
    }

    internal static bool ContainsExpectedWord(string text)
    {
        var lower = text.ToLowerInvariant();
        return ExpectedWordStems.Any(stem => lower.Contains(stem, StringComparison.Ordinal));
    }

    private static async Task<int> RunAsync(string wavPath, string reportPath)
    {
        var report = new Report();
        try
        {
            var paths = AppPaths.Default;
            Log.Paths = paths;
            Log.Info("smoke", $"Spit {Coordinator.ClientVersion} smoke test");

            var samples = LoadSamples(wavPath);
            report.AudioMs = (int)((long)samples.Length * 1000 / AudioCapture.SampleRate);
            report.HasSpeech = EnergyGate.HasSpeech(samples);

            using var models = new ModelDownloader(paths.Models);
            var file = ModelCatalog.DefaultFile;
            if (!models.IsDownloaded(file)) throw new FileNotFoundException($"{Strings.ModelNotDownloaded} in {paths.Models}", file);
            await using var transcriber = new WhisperTranscriber(models, file);
            report.AsrModel = transcriber.AsrModel;
            await transcriber.PrepareAsync(_ => { });
            report.Runtime = RuntimeOptions.LoadedLibrary?.ToString();
            report.RuntimeRequested = Environment.GetEnvironmentVariable(WhisperTranscriber.RuntimeVariable) ?? "auto";

            var hint = new TranscribeHint(Language: null, Vocabulary);
            var clock = Stopwatch.StartNew();
            var once = await transcriber.TranscribeAsync(samples, hint, progress: null);
            report.TranscribeMs = (int)clock.ElapsedMilliseconds;
            report.Text = once.Text;
            report.Language = once.Language;
            report.SkipGate = SkipGate.ReasonToClean(once.Text, "clean", once.Language)?.RawValue() ?? "skip";

            (report.StreamedText, report.StreamMs, report.StreamSegments) = await StreamAsync(transcriber, samples, report.AudioMs, hint);

            report.ExpectedWordsFound = ContainsExpectedWord(once.Text);
            report.Ok = once.Text.Length > 0 && report.ExpectedWordsFound;
            if (!report.Ok) report.Error = once.Text.Length == 0 ? "the transcription was empty" : "none of the fixture's expected words were transcribed";
        }
        catch (Exception e)
        {
            report.Ok = false;
            report.Error = $"{e.GetType().Name}: {e.Message}";
            Log.Failure("smoke", "smoke test", e);
        }

        report.BackendLog = WhisperTranscriber.BackendLog;
        try
        {
            File.WriteAllBytes(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Failure("smoke", "write the report", e);
            return 1;
        }
        return report.Ok ? 0 : 1;
    }

    /// The Coordinator's live path over a file: capture is simulated by releasing 100 ms of audio every 100 ms,
    /// then the key "comes up". The time reported is release to text — finish, tail pass and stitch.
    private static async Task<(string? Text, int Ms, int Segments)> StreamAsync(ISegmentTranscriber transcriber, float[] samples, int audioMs, TranscribeHint hint)
    {
        var chunk = AudioCapture.SampleRate * ChunkMs / 1000;
        var fed = new Feed();
        var session = new StreamingSession(transcriber, () => samples[..fed.Count], hint, TimeProvider.System, message => Log.Info("smoke", message));
        session.Start();
        for (var end = chunk; end < samples.Length + chunk; end += chunk)
        {
            await Task.Delay(ChunkMs);
            fed.Count = Math.Min(end, samples.Length);
        }

        var clock = Stopwatch.StartNew();
        var result = await session.FinishAsync();
        if (result is null) return (null, (int)clock.ElapsedMilliseconds, 0);
        var text = result.Text;
        if (StreamTail.Tail(samples, result.CoveredMs, audioMs) is { } tail)
        {
            var t = await transcriber.TranscribeAsync(tail, hint, progress: null);
            text = StreamTail.Combine(text, result.CoveredMs, t.Text);
        }
        return (text, (int)clock.ElapsedMilliseconds, result.Segments);
    }

    private static float[] Resample(float[] mono, int rate)
    {
        var resampler = new WdlResampler();
        resampler.SetMode(interp: true, filtercnt: 2, sinc: false);
        resampler.SetFilterParms();
        resampler.SetFeedMode(wantInputDriven: true);
        resampler.SetRates(rate, AudioCapture.SampleRate);
        var ratio = (double)AudioCapture.SampleRate / rate;

        var output = new List<float>((int)(mono.Length * ratio) + 64);
        var block = new float[(int)(ResampleBlockFrames * ratio) + 64];
        for (var offset = 0; offset < mono.Length;)
        {
            var frames = Math.Min(ResampleBlockFrames, mono.Length - offset);
            var accepted = Math.Min(resampler.ResamplePrepare(frames, 1, out var into), frames);
            if (accepted <= 0) break;
            mono.AsSpan(offset, accepted).CopyTo(into);
            var produced = resampler.ResampleOut(block, accepted, block.Length, 1);
            output.AddRange(block.AsSpan(0, produced));
            offset += accepted;
        }
        return output.ToArray();
    }

    private static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == flag && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) return args[i + 1];
        }
        return null;
    }

    private sealed class Feed
    {
        private int count;

        public int Count
        {
            get => Volatile.Read(ref count);
            set => Volatile.Write(ref count, value);
        }
    }

    /// CI reads `ok`, `text`, `error`, `asrModel`, `runtime`, `audioMs` and `transcribeMs`; the rest is for people.
    private sealed class Report
    {
        public bool Ok { get; set; }
        public string? Text { get; set; }
        public string? StreamedText { get; set; }
        public string? AsrModel { get; set; }
        public string? Runtime { get; set; }
        public string? RuntimeRequested { get; set; }
        public IReadOnlyList<string>? BackendLog { get; set; }
        public string? Language { get; set; }
        public int AudioMs { get; set; }
        public int TranscribeMs { get; set; }
        public int StreamMs { get; set; }
        public int StreamSegments { get; set; }
        public bool HasSpeech { get; set; }
        public string? SkipGate { get; set; }
        public bool ExpectedWordsFound { get; set; }
        public string? Error { get; set; }
    }
}

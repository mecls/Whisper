namespace Spit.Core;

// Port of mac/Voice/ASR/Transcriber.swift and mac/Voice/Refine/Refiner.swift.

public sealed record TranscribeHint(string? Language, IReadOnlyList<string> Vocabulary);

public sealed record Transcript(string Text, string Language, int DurationMs);

public interface ITranscriber
{
    bool IsReady { get; }
    Task PrepareAsync(Action<double> progress);
    Task<Transcript> TranscribeAsync(float[] samples, TranscribeHint hint, Action<double>? progress);
}

public interface IRefiner
{
    Task<RefineResult> RefineAsync(Dictation dictation, string mode);
    Task ReportInjectedAsync(Guid clientId, Injected injected, int? totalMs);
}

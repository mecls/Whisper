namespace Spit.Core;

/// Port of mac/Voice/Audio/EnergyGate.swift.
///
/// Energy gate: 95th percentile of 20 ms frame RMS must clear the threshold (~-40 dBFS). Rejects
/// silence (and Whisper's silence hallucinations) and single clicks.
public static class EnergyGate
{
    public const int DefaultSampleRate = 16000;
    public const float DefaultThreshold = 0.01f;

    public static float Rms(ReadOnlySpan<float> xs)
    {
        if (xs.IsEmpty) return 0;
        float acc = 0;
        foreach (var x in xs) acc += x * x;
        return MathF.Sqrt(acc / xs.Length);
    }

    public static bool HasSpeech(ReadOnlySpan<float> xs, int sampleRate = DefaultSampleRate, float threshold = DefaultThreshold)
    {
        var frame = sampleRate / 50;
        if (xs.Length < frame) return false;
        var levels = new float[xs.Length / frame];
        for (int n = 0, i = 0; i + frame <= xs.Length; n++, i += frame) levels[n] = Rms(xs.Slice(i, frame));
        Array.Sort(levels);
        var p95 = levels[Math.Min(levels.Length - 1, (int)(levels.Length * 0.95))];
        return p95 >= threshold;
    }
}

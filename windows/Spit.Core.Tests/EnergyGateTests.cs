namespace Spit.Core.Tests;

// Port of mac/VoiceTests/EnergyGateTests.swift.
public sealed class EnergyGateTests
{
    [Fact]
    public void testSilenceIsNotSpeech()
    {
        Assert.False(EnergyGate.HasSpeech(new float[16000]));
    }

    [Fact]
    public void testToneIsSpeech()
    {
        var tone = Enumerable.Range(0, 16000).Select(i => (float)Math.Sin(i * 2 * Math.PI * 220 / 16000) * 0.2f).ToArray();
        Assert.True(EnergyGate.HasSpeech(tone));
    }

    [Fact]
    public void testOneLoudClickInSilenceIsNotSpeech()
    {
        var xs = new float[16000];
        xs[8000] = 0.9f;
        Assert.False(EnergyGate.HasSpeech(xs)); // 95th percentile of 20 ms frames stays ~0
    }
}

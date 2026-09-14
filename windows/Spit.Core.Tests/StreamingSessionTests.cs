using Microsoft.Extensions.Time.Testing;

namespace Spit.Core.Tests;

/// The live-transcription loop that stands in for WhisperKit's `AudioStreamTranscriber` on Windows
/// (rules 22, 42). Not one of the 14 ported classes: on the Mac the loop is a dependency, not app code.
public sealed class StreamingSessionTests
{
    private const int Rate = EnergyGate.DefaultSampleRate;
    private static readonly TranscribeHint Hint = new(null, []);

    private static StreamSegment Seg(string text, float start, float end) => new(start, end, text);

    private static IReadOnlyList<StreamSegment> Segments(params StreamSegment[] segments) => segments;

    /// A 220 Hz tone at speech level (RMS ≈ 0.07, relative energy 1).
    private static float[] Voice(double seconds, float amplitude = 0.1f)
    {
        var xs = new float[(int)(seconds * Rate)];
        for (var i = 0; i < xs.Length; i++) xs[i] = amplitude * MathF.Sin(2 * MathF.PI * 220 * i / Rate);
        return xs;
    }

    private sealed class Audio
    {
        private float[] samples = [];
        public void Set(float[] xs) => Volatile.Write(ref samples, xs);
        public float[] Snapshot() => Volatile.Read(ref samples);
    }

    private sealed class FakeTranscriber : ISegmentTranscriber
    {
        private readonly Lock gate = new();
        private readonly List<int> lengths = [];
        private int calls;
        private int inFlight;
        private int maxInFlight;

        public Func<int, float[], Task<IReadOnlyList<StreamSegment>>> Respond { get; set; } =
            (_, _) => Task.FromResult(Segments());

        public int Calls { get { lock (gate) return calls; } }
        public int MaxInFlight { get { lock (gate) return maxInFlight; } }
        public int LengthOfCall(int index) { lock (gate) return lengths[index]; }

        public bool IsReady => true;
        public Task PrepareAsync(Action<double> progress) => Task.CompletedTask;
        public Task<Transcript> TranscribeAsync(float[] samples, TranscribeHint hint, Action<double>? progress) =>
            throw new NotSupportedException();

        public async Task<IReadOnlyList<StreamSegment>> TranscribeSegmentsAsync(float[] samples, TranscribeHint hint, CancellationToken ct)
        {
            int call;
            lock (gate)
            {
                call = ++calls;
                lengths.Add(samples.Length);
                maxInFlight = Math.Max(maxInFlight, ++inFlight);
            }
            try
            {
                return await Respond(call, samples);
            }
            finally
            {
                lock (gate) inFlight--;
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(2);
        }
    }

    /// Advances fake time in small steps until `condition` holds. Stepping, rather than one jump, is what
    /// keeps this independent of when the loop registers its next sleep.
    private static async Task Pump(FakeTimeProvider time, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            time.Advance(TimeSpan.FromMilliseconds(10));
            await Task.Delay(1);
        }
    }

    private static readonly IReadOnlyList<StreamSegment> FourSegments =
        Segments(Seg("One.", 0, 1), Seg(" Two.", 1, 2), Seg(" Three.", 2, 2.5f), Seg(" Four.", 2.5f, 3));

    [Fact]
    public async Task testNoPassRunsUntilMoreThanOneSecondOfNewAudio()
    {
        var audio = new Audio();
        audio.Set(Voice(1.0)); // exactly one second is not "more than one"
        var fake = new FakeTranscriber();
        var time = new FakeTimeProvider();
        var session = new StreamingSession(fake, audio.Snapshot, Hint, time);

        session.Start();
        await Pump(time, () => session.SkippedPolls >= 5);
        Assert.Equal(0, fake.Calls);

        audio.Set(Voice(1.1));
        await Pump(time, () => fake.Calls == 1);
        Assert.Null(await session.FinishAsync());
    }

    [Fact]
    public async Task testAPassConfirmsAllButTheLastTwoSegments()
    {
        var audio = new Audio();
        audio.Set(Voice(3));
        var fake = new FakeTranscriber { Respond = (_, _) => Task.FromResult(FourSegments) };
        var session = new StreamingSession(fake, audio.Snapshot, Hint, new FakeTimeProvider());

        Assert.True(await session.TickAsync());

        Assert.Equal("One. Two.", session.ConfirmedText);
        Assert.Equal("Three. Four.", session.UnconfirmedText);
        Assert.Equal(4, session.SegmentCount);
        Assert.Equal(3000, session.CoveredMs);
    }

    [Fact]
    public async Task testTheNextPassStartsWhereConfirmedTextEnds()
    {
        var audio = new Audio();
        audio.Set(Voice(3));
        var fake = new FakeTranscriber
        {
            Respond = (call, _) => Task.FromResult(call == 1
                ? FourSegments
                // Relative to the clipped audio, which begins at the 2 s the first pass confirmed up to.
                : Segments(Seg(" Three.", 0, 0.5f), Seg(" Four.", 0.5f, 1), Seg(" Five.", 1, 2), Seg(" Six.", 2, 2.5f))),
        };
        var session = new StreamingSession(fake, audio.Snapshot, Hint, new FakeTimeProvider());
        Assert.True(await session.TickAsync());

        audio.Set(Voice(4.5));
        Assert.True(await session.TickAsync());

        Assert.Equal((int)(2.5 * Rate), fake.LengthOfCall(1));
        Assert.Equal("One. Two. Three. Four.", session.ConfirmedText);
        Assert.Equal("Five. Six.", session.UnconfirmedText);
        Assert.Equal(4500, session.CoveredMs);
    }

    [Fact]
    public async Task testSilenceRunsNoPass()
    {
        var audio = new Audio();
        audio.Set(new float[3 * Rate]);
        var fake = new FakeTranscriber { Respond = (_, _) => Task.FromResult(FourSegments) };
        var session = new StreamingSession(fake, audio.Snapshot, Hint, new FakeTimeProvider());

        Assert.False(await session.TickAsync());
        // Room tone: RMS ≈ 0.0035, relative energy ≈ 0.1, under the 0.3 threshold.
        audio.Set(Voice(3, amplitude: 0.005f));
        Assert.False(await session.TickAsync());

        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task testFinishReturnsConfirmedAndUnconfirmedText()
    {
        var audio = new Audio();
        audio.Set(Voice(3));
        var fake = new FakeTranscriber { Respond = (_, _) => Task.FromResult(FourSegments) };
        var session = new StreamingSession(fake, audio.Snapshot, Hint, new FakeTimeProvider());
        Assert.True(await session.TickAsync());

        var result = await session.FinishAsync();

        Assert.NotNull(result);
        Assert.Equal("One. Two. Three. Four.", result.Text);
        Assert.Equal(4, result.Segments);
        Assert.Equal(3000, result.CoveredMs);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task testFinishReturnsNullWhenNothingWasTranscribed()
    {
        var audio = new Audio();
        var fake = new FakeTranscriber();

        Assert.Null(await new StreamingSession(fake, audio.Snapshot, Hint, new FakeTimeProvider()).FinishAsync());

        audio.Set(new float[3 * Rate]);
        var time = new FakeTimeProvider();
        var silent = new StreamingSession(fake, audio.Snapshot, Hint, time);
        silent.Start();
        await Pump(time, () => silent.SkippedPolls >= 3);
        Assert.Null(await silent.FinishAsync());

        audio.Set(Voice(2));
        var empty = new StreamingSession(fake, audio.Snapshot, Hint, new FakeTimeProvider());
        Assert.True(await empty.TickAsync());
        Assert.Null(await empty.FinishAsync());
    }

    [Fact]
    public async Task testFinishWaitsForThePassInFlightAndStartsNoOther()
    {
        var audio = new Audio();
        audio.Set(Voice(2));
        var release = new TaskCompletionSource<IReadOnlyList<StreamSegment>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeTranscriber
        {
            Respond = (call, _) => call == 1 ? release.Task : Task.FromResult(Segments(Seg(" Later.", 0, 1))),
        };
        var time = new FakeTimeProvider();
        var session = new StreamingSession(fake, audio.Snapshot, Hint, time);

        session.Start();
        await WaitUntil(() => fake.Calls == 1);

        var finishing = session.FinishAsync();
        // Enough new audio for another pass, and time for the loop to want one.
        audio.Set(Voice(5));
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(finishing.IsCompleted);

        release.SetResult(Segments(Seg("Hello there.", 0, 2)));
        var result = await finishing;

        Assert.NotNull(result);
        Assert.Equal("Hello there.", result.Text);
        Assert.Equal(1, result.Segments);
        Assert.Equal(2000, result.CoveredMs);

        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, fake.Calls);
        Assert.Equal(1, fake.MaxInFlight);
    }

    [Fact]
    public async Task testAFailingPassEndsTheStreamWithoutThrowing()
    {
        var audio = new Audio();
        audio.Set(Voice(2));
        var fake = new FakeTranscriber { Respond = (_, _) => throw new InvalidOperationException("model gone") };
        var session = new StreamingSession(fake, audio.Snapshot, Hint, new FakeTimeProvider());

        session.Start();
        await WaitUntil(() => fake.Calls == 1);

        Assert.Null(await session.FinishAsync());
    }
}

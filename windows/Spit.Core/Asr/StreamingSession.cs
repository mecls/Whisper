namespace Spit.Core;

/// What a finished stream hands the Coordinator: the text to paste, how many segments it was assembled
/// from (the skip gate needs it) and how far into the dictation those segments reach (the tail pass needs it).
public sealed record StreamResult(string Text, int Segments, int CoveredMs);

/// <summary>
/// Live transcription for Windows: WhisperKit's `AudioStreamTranscriber` loop, which the Mac runs as a
/// dependency, rebuilt on `StreamingPolicy` because whisper.cpp has no loop of its own (rules 22, 42).
/// Mirrors mac/Voice/ASR/StreamingTranscriber.swift for everything the Coordinator sees.
///
/// One pass at a time, always: the loop awaits each inference before it polls again, and once
/// `FinishAsync` has begun no new pass starts — so the tail pass that follows never shares the model with
/// this loop.
///
/// Text is never logged, only counts, lengths and timings (rule 25).
/// </summary>
public sealed class StreamingSession
{
    private readonly ISegmentTranscriber transcriber;
    private readonly Func<float[]> samples;
    private readonly TranscribeHint hint;
    private readonly TimeProvider time;
    private readonly Action<string>? log;
    private readonly CancellationTokenSource sleep = new();
    private readonly Lock gate = new();

    // Guarded by `gate`: read from the UI thread while the loop writes them.
    private StreamConfirmation state = StreamConfirmation.Empty;
    private double coveredSeconds;
    private bool started;
    private bool stopRequested;
    private Task? loop;

    // Touched only by the pass in progress, and there is only ever one.
    private int lastBufferSize;
    private int passes;
    private int skippedPolls;

    /// <param name="samples">Everything captured this dictation, without consuming it (16 kHz mono).</param>
    /// <param name="log">Receives lengths and timings only.</param>
    public StreamingSession(ISegmentTranscriber transcriber, Func<float[]> samples, TranscribeHint hint, TimeProvider? time = null, Action<string>? log = null)
    {
        this.transcriber = transcriber;
        this.samples = samples;
        this.hint = hint;
        this.time = time ?? TimeProvider.System;
        this.log = log;
    }

    /// Raised on the loop's thread after every pass; marshal before touching UI.
    public event Action? Changed;

    /// Text Whisper has settled on and will not revise.
    public string ConfirmedText { get { lock (gate) return StreamingPolicy.JoinSegments(state.Confirmed); } }

    /// The last two segments of the latest pass: shown dimmed while the stream runs, pasted at the end.
    public string UnconfirmedText { get { lock (gate) return StreamingPolicy.JoinSegments(state.Unconfirmed); } }

    /// How far into the dictation the segments reach, never moving backwards.
    public int CoveredMs { get { lock (gate) return (int)(coveredSeconds * 1000); } }

    /// Segments the transcript is assembled from so far, unconfirmed included (see `SkipGate`).
    public int SegmentCount { get { lock (gate) return StreamingPolicy.SegmentCount(state.Confirmed, state.Unconfirmed); } }

    /// `StreamingTranscriber.isRunning`: started and not yet told to finish.
    public bool IsRunning { get { lock (gate) return started && !stopRequested; } }

    internal int Passes => Volatile.Read(ref passes);
    internal int SkippedPolls => Volatile.Read(ref skippedPolls);

    public void Start()
    {
        lock (gate)
        {
            if (started || stopRequested) return;
            started = true;
            loop = Task.Run(RunAsync);
        }
        log?.Invoke("live transcription starting");
    }

    /// <summary>
    /// Ends the stream and returns the text to paste, or null when there is nothing usable and the caller
    /// should fall back to a one-pass transcription.
    ///
    /// A pass already running is waited for, never cancelled: its segments belong in the transcript, and
    /// the model has to be free before the tail pass starts (rule 42). Confirmed *and* unconfirmed text is
    /// returned — the last two segments are permanently unconfirmed because no more audio is coming, and
    /// leaving them out truncated every streamed dictation on the Mac.
    /// </summary>
    public async Task<StreamResult?> FinishAsync()
    {
        Task? running;
        lock (gate)
        {
            stopRequested = true;
            running = loop;
        }
        // Wakes a loop sleeping between polls; it has no effect on an inference in flight.
        sleep.Cancel();
        if (running is not null) await running.ConfigureAwait(false);

        IReadOnlyList<StreamSegment> confirmed, unconfirmed;
        double covered;
        lock (gate)
        {
            confirmed = state.Confirmed;
            unconfirmed = state.Unconfirmed;
            covered = coveredSeconds;
        }

        var text = StreamingPolicy.FinalText(confirmed, unconfirmed);
        if (text is null)
        {
            // Loud on purpose. This is the silent-fallback path, and on the Mac a wrong VAD scale sent every
            // dictation down it for hours without a line in the log.
            log?.Invoke("live transcription produced nothing — falling back to a one-pass transcription");
            return null;
        }

        // Spans, not text: a hole between one segment's end and the next one's start is audio Whisper
        // produced no words for, and it is the only thing that localises words lost mid-dictation.
        StreamSegment[] spans = [.. confirmed, .. unconfirmed];
        var map = string.Join(' ', spans.Select(s => $"{(int)(s.Start * 1000)}-{(int)(s.End * 1000)}"));
        var holes = spans.Zip(spans.Skip(1))
            .Where(p => p.Second.Start - p.First.End > 0.25f)
            .Select(p => $"{(int)(p.First.End * 1000)}-{(int)(p.Second.Start * 1000)}")
            .ToArray();
        var segments = StreamingPolicy.SegmentCount(confirmed, unconfirmed);
        var coveredMs = (int)(covered * 1000);
        log?.Invoke($"live transcription used: {segments} segments, covering {coveredMs} ms of audio");
        log?.Invoke($"segment spans: {map}{(holes.Length == 0 ? "" : "  HOLES: " + string.Join(' ', holes))}");
        return new StreamResult(text, Math.Max(1, segments), coveredMs);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            bool ran;
            try
            {
                ran = await TickAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Never fatal. The buffered audio is still there and the dictation falls back to a single
                // pass at the end — losing a dictation to a streaming failure is the one outcome this must
                // not produce.
                log?.Invoke($"live transcription unavailable: {e.GetType().Name}");
                return;
            }

            lock (gate)
            {
                if (stopRequested) return;
            }
            // After a pass the loop asks again straight away, as `realtimeLoop` does; it sleeps only when
            // it decided not to transcribe.
            if (ran) continue;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(StreamingPolicy.PollIntervalMs), time, sleep.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// One iteration of `transcribeCurrentBuffer`: runs a pass and returns true, or returns false when there
    /// is under a second of new audio, no voice in it, or the session is finishing.
    internal async Task<bool> TickAsync()
    {
        var buffer = samples();
        if (!StreamingPolicy.ShouldRunPass(buffer.Length, lastBufferSize, RelativeEnergy(buffer, lastBufferSize)))
        {
            // Deferred, not dropped: `lastBufferSize` has not moved, so the next pass that does see voice
            // picks this audio up.
            Interlocked.Increment(ref skippedPolls);
            return false;
        }

        float clipSeconds;
        lock (gate)
        {
            // The last moment before inference, checked under the same lock `FinishAsync` sets it with.
            if (stopRequested) return false;
            lastBufferSize = buffer.Length;
            clipSeconds = state.LastConfirmedSegmentEndSeconds;
        }

        // WhisperKit's `clipTimestamps`: decoding starts where confirmed text ends, and the times that
        // come back are relative to the audio handed over, so they are shifted back onto the dictation.
        var offset = Math.Clamp((int)((double)clipSeconds * EnergyGate.DefaultSampleRate), 0, buffer.Length);
        var began = time.GetTimestamp();
        var relative = await transcriber.TranscribeSegmentsAsync(buffer[offset..], hint, CancellationToken.None).ConfigureAwait(false);
        var absolute = new StreamSegment[relative.Count];
        for (var i = 0; i < relative.Count; i++)
        {
            absolute[i] = relative[i] with { Start = relative[i].Start + clipSeconds, End = relative[i].End + clipSeconds };
        }

        int count, chars;
        lock (gate)
        {
            state = StreamingPolicy.Confirm(state, absolute);
            coveredSeconds = StreamingPolicy.CoveredSeconds(coveredSeconds, state.Confirmed, state.Unconfirmed);
            count = StreamingPolicy.SegmentCount(state.Confirmed, state.Unconfirmed);
            chars = StreamingPolicy.JoinSegments(state.Confirmed).Length;
        }
        Interlocked.Increment(ref passes);
        // Lengths only, never content: this fires many times per dictation.
        log?.Invoke($"stream pass: {buffer.Length - offset} samples in {(int)time.GetElapsedTime(began).TotalMilliseconds} ms; {count} segments, {chars} confirmed chars");
        Changed?.Invoke();
        return true;
    }

    /// The VAD's input: one relative-energy value per `StreamingPolicy.EnergyValueMs` of the audio that
    /// arrived since the last pass, which is the granularity `isVoiceDetected` assumes. Measured on the same
    /// 16 kHz samples the bar's waveform is drawn from, so the two cannot disagree about what is speech.
    internal static float[] RelativeEnergy(float[] buffer, int from)
    {
        var frame = EnergyGate.DefaultSampleRate * StreamingPolicy.EnergyValueMs / 1000;
        from = Math.Clamp(from, 0, buffer.Length);
        var values = new float[(buffer.Length - from) / frame];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = StreamingPolicy.RelativeEnergy(EnergyGate.Rms(buffer.AsSpan(from + i * frame, frame)));
        }
        return values;
    }
}

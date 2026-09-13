using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using Spit.Core;

namespace Spit.App;

/// <summary>
/// Port of mac/Voice/Audio/AudioRecorder.swift: the default microphone → 16 kHz mono Float32 → `RingBuffer`.
/// Nothing is written to disk.
///
/// WASAPI shared mode on the default capture endpoint for `Role.Console` — the device Sound settings calls
/// the default input, which is the one the user chose. `Role.Communications` is what call apps use and can be
/// a headset nobody meant for dictation.
///
/// Every public member is called on the dispatcher. Audio arrives on NAudio's capture thread and touches only
/// the ring buffer (which has its own lock) and the converter built for that one recorder.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    /// `AudioRecorder.sampleRate`.
    public const int SampleRate = EnergyGate.DefaultSampleRate;
    /// `AudioRecorder.maxSeconds`.
    public const int MaxSeconds = 90;
    /// `AudioRecorder.scheduleIdleStop`: capture stays open this long after a dictation so the next one's first syllable is not clipped.
    public static readonly TimeSpan WarmKeep = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan DeviceSettle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StopReportWait = TimeSpan.FromMilliseconds(200);
    private const int LevelFrame = SampleRate / 50;
    private const string Category = "audio";

    private readonly SynchronizationContext dispatcher;
    private readonly TimeProvider time;
    private readonly RingBuffer buffer = new(SampleRate * MaxSeconds);
    private readonly SendOrPostCallback raiseLevel;
    private readonly SendOrPostCallback raiseCapReached;

    private MMDeviceEnumerator? enumerator;
    private MMDeviceNotificationClient? notifications;
    private Input? input;
    private ITimer? idleStop;
    private int idleGeneration;
    private ITimer? settle;
    private long startedAt;
    private volatile bool capturing;
    private int capSignalled;
    private bool disposed;

    /// <param name="dispatcher">Where `Level` and `CapReached` are raised, and where device changes are handled.</param>
    public AudioCapture(SynchronizationContext dispatcher, TimeProvider? time = null)
    {
        this.dispatcher = dispatcher;
        this.time = time ?? TimeProvider.System;
        raiseLevel = state => Level?.Invoke((float)state!);
        raiseCapReached = _ =>
        {
            if (capturing) CapReached?.Invoke();
        };
    }

    /// RMS of each 20 ms of captured audio, for the bar's waveform. On the dispatcher.
    public event Action<float>? Level;

    /// The 90-second buffer is full; the Coordinator stops the dictation. On the dispatcher.
    public event Action? CapReached;

    public bool IsCapturing => capturing;

    /// Whether the device is open and streaming — during a dictation, or warm after one.
    public bool IsRunning => input?.Recorder.CaptureState is CaptureState.Starting or CaptureState.Capturing;

    /// Everything captured so far this dictation, without consuming it. For live transcription.
    public float[] Snapshot() => buffer.Snapshot();

    /// <summary>
    /// Opens the default device ahead of the first dictation. Throws `NoMicrophoneException` when there is none.
    /// </summary>
    public void Prepare()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // Subscribed before the first open, deliberately: with no microphone at launch the open throws, and the
        // notification is then the only thing that tells us one has appeared.
        ObserveDeviceChanges();
        if (input is null) Open();
    }

    /// <summary>
    /// Begins a dictation. Throws `MicrophoneBlockedException` when Windows privacy settings refuse the
    /// microphone and `NoMicrophoneException` for anything else (rule 39).
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CancelIdleStop();
        ObserveDeviceChanges();

        var current = input;
        // The default device may have changed while capture sat idle, and a notification can go missing. Cheap
        // to check, and it is the difference between the first dictation after plugging in a headset working
        // and silently recording the old device.
        if (current is not null
            && (current.Recorder.CaptureState is not (CaptureState.Capturing or CaptureState.Stopped) || !IsDefaultDevice(current.DeviceId)))
        {
            Close("input changed while idle");
            current = null;
        }
        current ??= Open();

        buffer.Drain();
        Interlocked.Exchange(ref capSignalled, 0);
        startedAt = time.GetTimestamp();
        capturing = true;
        if (current.Recorder.CaptureState == CaptureState.Capturing) return;
        try
        {
            StartRecorder(current);
        }
        catch
        {
            capturing = false;
            throw;
        }
    }

    public (float[] samples, int ms) Stop()
    {
        capturing = false;
        var ms = startedAt == 0 ? 0 : (int)time.GetElapsedTime(startedAt).TotalMilliseconds;
        ScheduleIdleStop();
        return (buffer.Drain(), ms);
    }

    public void Discard()
    {
        capturing = false;
        buffer.Drain();
        ScheduleIdleStop();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        capturing = false;
        CancelIdleStop();
        settle?.Dispose();
        // Before the enumerator: the subscription unregisters through it.
        notifications?.Dispose();
        Close("shutdown");
        enumerator?.Dispose();
        buffer.Drain();
    }

    private Input Open()
    {
        enumerator ??= new MMDeviceEnumerator();
        MMDevice? device = null;
        WasapiRecorder? recorder = null;
        try
        {
            if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out device))
            {
                HookLog.Error(Category, "no default capture device");
                throw new NoMicrophoneException();
            }

            // Built with no synchronisation context: WasapiRecorder captures the current one and would post
            // RecordingStopped to the dispatcher — where `StartRecorder`, which blocks the dispatcher while it
            // waits for the stream to start, could never see a start failure.
            var saved = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                recorder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithEventSync().Build();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(saved);
            }

            var opened = new Input(device, recorder);
            recorder.DataAvailable += (packet, _, _, _) => OnData(opened, packet);
            recorder.RecordingStopped += (_, e) => OnRecordingStopped(opened, e.Exception);
            input = opened;
            var format = recorder.WaveFormat;
            HookLog.Info(Category, $"capture opened: {format.SampleRate} Hz, {format.Channels} ch, {format.BitsPerSample}-bit {format.Encoding}");
            return opened;
        }
        catch (Exception e)
        {
            recorder?.Dispose();
            device?.Dispose();
            if (e is not NoMicrophoneException) HookLog.Error(Category, $"could not open the capture device: {HookLog.Describe(e)}");
            throw MicrophoneAccess.Classify(e);
        }
    }

    /// <summary>
    /// WasapiRecorder initialises the audio client on this thread — which is where access denied surfaces — and
    /// starts the stream on its own thread, where a failure arrives only as a stop. Waiting for the state to
    /// settle turns both into an exception here, so the Coordinator can say what went wrong instead of showing
    /// "Listening" over a microphone recording nothing.
    /// </summary>
    private void StartRecorder(Input source)
    {
        source.Stopped.Reset();
        source.StopError = null;
        try
        {
            source.Recorder.StartRecording();
        }
        catch (Exception e)
        {
            HookLog.Error(Category, $"could not start capture: {HookLog.Describe(e)}");
            Close("start failed");
            throw MicrophoneAccess.Classify(e);
        }

        var clock = Stopwatch.StartNew();
        while (source.Recorder.CaptureState == CaptureState.Starting && clock.Elapsed < StartTimeout) Thread.Sleep(5);

        var state = source.Recorder.CaptureState;
        if (state == CaptureState.Capturing)
        {
            HookLog.Info(Category, $"capture started in {clock.ElapsedMilliseconds} ms");
            return;
        }

        Exception? error = null;
        if (state == CaptureState.Stopped)
        {
            // The state flips to Stopped just before the stop is reported; give the report a moment to land.
            source.Stopped.Wait(StopReportWait);
            error = source.StopError;
        }
        HookLog.Error(Category, state == CaptureState.Starting
            ? $"capture did not start within {StartTimeout.TotalSeconds:0} s"
            : $"capture stopped while starting: {(error is null ? "no error reported" : HookLog.Describe(error))}");
        Close("start failed");
        throw error is null ? new NoMicrophoneException() : MicrophoneAccess.Classify(error);
    }

    private void Close(string reason)
    {
        var closing = input;
        if (closing is null) return;
        input = null;
        try
        {
            // Joins the capture thread, so no packet is still being converted once this returns.
            closing.Recorder.Dispose();
        }
        catch (Exception e)
        {
            HookLog.Error(Category, $"closing capture ({reason}) failed: {HookLog.Describe(e)}");
        }
        closing.Device.Dispose();
        HookLog.Info(Category, $"capture closed ({reason})");
    }

    private void ScheduleIdleStop()
    {
        CancelIdleStop();
        var generation = idleGeneration;
        idleStop = time.CreateTimer(_ => dispatcher.Post(_ => IdleStop(generation), null), null, WarmKeep, Timeout.InfiniteTimeSpan);
    }

    private void CancelIdleStop()
    {
        // The generation retires a timer that already fired and posted before it was cancelled.
        idleGeneration++;
        idleStop?.Dispose();
        idleStop = null;
    }

    private void IdleStop(int generation)
    {
        if (generation != idleGeneration || capturing || disposed) return;
        // WASAPI cannot initialise a client twice, so a stopped recorder cannot be restarted: stopping means
        // closing, and the next Start opens the device again.
        Close("idle");
    }

    private void ObserveDeviceChanges()
    {
        if (notifications is not null) return;
        try
        {
            enumerator ??= new MMDeviceEnumerator();
            // Raised on the audio service's own thread, which must not block or call back into the audio stack;
            // the handler only posts.
            notifications = enumerator.CreateNotificationClient(useSynchronizationContext: false);
            notifications.DefaultDeviceChanged += OnDefaultDeviceChanged;
        }
        catch (Exception e)
        {
            HookLog.Error(Category, $"could not watch for device changes: {HookLog.Describe(e)}");
        }
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow != DataFlow.Capture || e.Role != Role.Console) return;
        dispatcher.Post(_ => ScheduleRebuild(), null);
    }

    private void ScheduleRebuild()
    {
        if (disposed) return;
        // Rebuilding the instant the notification lands catches the audio stack mid-handover; the Mac waits
        // 300 ms for the same reason. A burst of notifications collapses into one rebuild.
        settle?.Dispose();
        settle = time.CreateTimer(_ => dispatcher.Post(_ => Rebuild("default device changed"), null), null, DeviceSettle, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Rule 39, the Mac's AirPods rule: capture follows the default device, and samples already in the ring
    /// buffer — converted to 16 kHz mono before they were stored — stay valid, so a dictation in progress
    /// simply continues on the new microphone.
    /// </summary>
    private void Rebuild(string reason, bool force = false)
    {
        if (disposed) return;
        var current = input;
        // Remember whether capture is owed a restart before touching anything.
        var shouldRun = capturing || IsRunning;
        if (current is null && !shouldRun) return;
        if (!force && current is not null && IsDefaultDevice(current.DeviceId)) return;

        Close(reason);
        if (!shouldRun) return;
        try
        {
            StartRecorder(Open());
            HookLog.Info(Category, $"audio input rebuilt after {reason}");
        }
        catch (Exception e)
        {
            // Nothing to listen to right now. `capturing` stays as it was: if this happened mid-dictation the
            // user still gets whatever was captured before the device vanished, rather than an error they
            // cannot act on. The next device notification tries again.
            HookLog.Error(Category, $"could not rebuild audio input after {reason}: {HookLog.Describe(e)}");
        }
    }

    private bool IsDefaultDevice(string? id)
    {
        try
        {
            enumerator ??= new MMDeviceEnumerator();
            if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out var device)) return false;
            using (device) return device.ID == id;
        }
        catch (Exception e)
        {
            // Unknown is treated as unchanged: keep the device that is working.
            HookLog.Error(Category, $"could not read the default capture device: {HookLog.Describe(e)}");
            return true;
        }
    }

    /// Capture thread.
    private void OnData(Input source, ReadOnlySpan<byte> packet)
    {
        if (!capturing || packet.IsEmpty) return;
        try
        {
            var samples = source.Converter.Convert(packet);
            if (samples.IsEmpty) return;
            buffer.Write(samples);
            source.Converter.PostLevels(samples, dispatcher, raiseLevel);
            if (buffer.IsFull && Interlocked.Exchange(ref capSignalled, 1) == 0) dispatcher.Post(raiseCapReached, null);
        }
        catch (Exception e)
        {
            // Escaping DataAvailable would stop the recorder; one bad packet is not worth a lost dictation.
            if (Interlocked.Exchange(ref source.ConversionFailed, 1) == 0)
            {
                HookLog.Error(Category, $"audio conversion failed: {HookLog.Describe(e)}");
            }
        }
    }

    /// Capture thread, for a stop requested by us (no error) or forced by the device (error).
    private void OnRecordingStopped(Input source, Exception? error)
    {
        source.StopError = error;
        source.Stopped.Set();
        if (error is not null) dispatcher.Post(_ => OnCaptureFailed(source, error), null);
    }

    private void OnCaptureFailed(Input source, Exception error)
    {
        // Already closed or replaced on purpose: nothing to recover.
        if (disposed || !ReferenceEquals(input, source)) return;
        HookLog.Error(Category, $"capture stopped: {HookLog.Describe(error)}");
        Rebuild("capture failure", force: true);
    }

    /// One opened device and everything tied to it, replaced as a whole on a device change, so an old
    /// recorder's thread can never write through a converter built for a new format.
    private sealed class Input
    {
        public Input(MMDevice device, WasapiRecorder recorder)
        {
            Device = device;
            Recorder = recorder;
            DeviceId = recorder.DeviceId;
            Converter = new Converter(recorder.WaveFormat);
        }

        public MMDevice Device { get; }
        public WasapiRecorder Recorder { get; }
        public string DeviceId { get; }
        public Converter Converter { get; }
        public ManualResetEventSlim Stopped { get; } = new();
        public volatile Exception? StopError;
        public int ConversionFailed;
    }

    /// <summary>
    /// Device format → 16 kHz mono Float32. Capture thread only.
    ///
    /// The mix format is decoded by NAudio's sample providers (PCM or float, WAVE_FORMAT_EXTENSIBLE included),
    /// channels are averaged, and the rate is converted by NAudio's managed WDL resampler — driven by input,
    /// not through `WdlResamplingSampleProvider`. That provider pulls a fixed amount and treats a short read as
    /// the end of the stream, padding it with zeros; on a live microphone every packet is a short read.
    /// </summary>
    private sealed class Converter
    {
        private readonly BufferedWaveProvider bytes;
        private readonly ISampleProvider decoded;
        private readonly int channels;
        private readonly int bytesPerSample;
        private readonly WdlResampler? resampler;
        private readonly double ratio = 1;
        private float[] interleaved = [];
        private float[] mono = [];
        private float[] output = [];
        private double levelSum;
        private int levelCount;

        public Converter(WaveFormat format)
        {
            bytes = new BufferedWaveProvider(format, TimeSpan.FromSeconds(1)) { ReadFully = false, DiscardOnBufferOverflow = true };
            decoded = bytes.ToSampleProvider();
            channels = Math.Max(1, decoded.WaveFormat.Channels);
            bytesPerSample = Math.Max(1, format.BlockAlign / Math.Max(1, format.Channels));
            var rate = decoded.WaveFormat.SampleRate;
            if (rate == SampleRate) return;
            resampler = new WdlResampler();
            resampler.SetMode(interp: true, filtercnt: 2, sinc: false);
            resampler.SetFilterParms();
            resampler.SetFeedMode(wantInputDriven: true);
            resampler.SetRates(rate, SampleRate);
            ratio = (double)SampleRate / rate;
        }

        /// The converted samples for one packet; valid until the next call.
        public ReadOnlySpan<float> Convert(ReadOnlySpan<byte> packet)
        {
            bytes.AddSamples(packet);
            var expected = packet.Length / bytesPerSample;
            if (interleaved.Length < expected) interleaved = new float[expected];
            var total = 0;
            int read;
            while (total < expected && (read = decoded.Read(interleaved.AsSpan(total, expected - total))) > 0) total += read;

            var frames = total / channels;
            if (mono.Length < frames) mono = new float[frames];
            if (channels == 1)
            {
                interleaved.AsSpan(0, frames).CopyTo(mono);
            }
            else
            {
                for (var f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (var c = 0; c < channels; c++) sum += interleaved[f * channels + c];
                    mono[f] = sum / channels;
                }
            }
            if (resampler is null) return mono.AsSpan(0, frames);

            var accepted = Math.Min(resampler.ResamplePrepare(frames, 1, out var into), frames);
            mono.AsSpan(0, accepted).CopyTo(into);
            var capacity = (int)(frames * ratio) + 64;
            if (output.Length < capacity) output = new float[capacity];
            var produced = resampler.ResampleOut(output, accepted, capacity, 1);
            return output.AsSpan(0, produced);
        }

        /// Folds samples into 20 ms frames and posts each frame's RMS (`EnergyGate.Rms`), which is what the
        /// bar's waveform draws.
        public void PostLevels(ReadOnlySpan<float> samples, SynchronizationContext target, SendOrPostCallback callback)
        {
            foreach (var x in samples)
            {
                levelSum += (double)x * x;
                if (++levelCount < LevelFrame) continue;
                target.Post(callback, (float)Math.Sqrt(levelSum / levelCount));
                levelSum = 0;
                levelCount = 0;
            }
        }
    }
}

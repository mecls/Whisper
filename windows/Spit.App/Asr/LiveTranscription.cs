using Spit.Core;

namespace Spit.App;

/// <summary>
/// The Coordinator's handle on live transcription: a `StreamingSession` per dictation, reading the running
/// capture and reporting back on the dispatcher. The Windows counterpart of the Mac's
/// `StreamingTranscriber` + `VoiceAudioProcessor` pair — capture stays with `AudioCapture`, where the device
/// rebuild, the 90 s cap and the waveform live, and the stream only reads what it has captured.
///
/// Main-thread only, like the Mac type.
/// </summary>
public sealed class LiveTranscription
{
    private readonly AudioCapture capture;
    private readonly SynchronizationContext dispatcher;
    private readonly TimeProvider time;
    private StreamingSession? session;

    public LiveTranscription(AudioCapture capture, SynchronizationContext dispatcher, TimeProvider? time = null)
    {
        this.capture = capture;
        this.dispatcher = dispatcher;
        this.time = time ?? TimeProvider.System;
    }

    /// Raised on the dispatcher whenever the text changes, for the bar.
    public event Action? Changed;

    public bool IsRunning => session?.IsRunning == true;

    public string ConfirmedText => session?.ConfirmedText ?? "";

    public string UnconfirmedText => session?.UnconfirmedText ?? "";

    /// Of the latest session, and still readable after `FinishAsync`: the tail pass compares it with the
    /// recorded length.
    public int CoveredMs => session?.CoveredMs ?? 0;

    /// Begins streaming off the already-running capture. A session still running here belongs to a dictation that
    /// ended without finishing it, so it is finished and its result dropped. The Mac leaves it alone; inheriting it
    /// pastes the previous dictation's words and skips this one's opening audio.
    public void Start(ISegmentTranscriber transcriber, TranscribeHint hint)
    {
        if (session is { IsRunning: true } stale) _ = FinishStaleAsync(stale);
        var next = new StreamingSession(transcriber, capture.Snapshot, hint, time, message => HookLog.Info("streaming", message));
        next.Changed += () => dispatcher.Post(_ =>
        {
            // A late pass from a session that has since been replaced must not repaint the new one.
            if (ReferenceEquals(session, next)) Changed?.Invoke();
        }, null);
        session = next;
        next.Start();
        Changed?.Invoke();
    }

    /// Ends the stream once any pass in flight has finished; null means fall back to a one-pass transcription.
    public Task<StreamResult?> FinishAsync() => session?.FinishAsync() ?? Task.FromResult<StreamResult?>(null);

    private static async Task FinishStaleAsync(StreamingSession stale)
    {
        try
        {
            await stale.FinishAsync();
        }
        catch (Exception e)
        {
            HookLog.Error("streaming", $"finish a stale stream: {HookLog.Describe(e)}");
        }
    }
}

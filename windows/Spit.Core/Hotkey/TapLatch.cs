namespace Spit.Core;

/// Port of mac/Voice/Hotkey/TapLatch.swift.
///
/// Decides whether a key gesture is a hold, a double-tap that latches, or a stray tap. Pure and
/// clock-free: every method takes the time as a parameter and the coordinator supplies it, so the
/// gesture tests run without sleeping.
///
/// Latching is free because of `Handle(Release)`: a press shorter than `DictationMachine.MinimumMs`
/// is already thrown away by the reducer as too short to be a dictation, so holding its stop open for
/// a moment costs nothing — while a real hold stops the instant the key comes up.
public sealed class TapLatch
{
    /// How long after a short release a second press still counts as a double-tap.
    public const int WindowMs = 300;

    public enum Outcome
    {
        /// Begin recording. The dictation is real from this moment, even if it later turns out to
        /// have been a stray tap.
        StartDictation,
        /// The key came up too quickly to be a dictation. Keep recording and start a timer for
        /// `TapLatch.WindowMs`, calling `WindowExpired` if it fires. Carries no deadline: the caller
        /// knows `WindowMs`, which is all it needs to schedule the timer.
        HoldOpen,
        /// A second press landed inside the window. The recording already running becomes
        /// hands-free; nothing is restarted, so audio from the first tap is kept.
        Latch,
        /// Stop recording and transcribe. Covers both a normal hold-release and the press that ends
        /// a latched session.
        EndSession,
        /// The window closed with no second press. Discard; this was a stray key brush.
        Abandon,
    }

    private abstract record State
    {
        public sealed record Idle : State;
        public sealed record Recording(DateTimeOffset PressedAt) : State;
        public sealed record WindowOpen(DateTimeOffset ReleasedAt) : State;
        public sealed record Latched : State;
        /// The press that ended a latched session has been handled; its release must be swallowed
        /// so it does not read as the start of something new.
        public sealed record SwallowingRelease : State;
    }

    private State state = new State.Idle();

    /// Elapsed milliseconds, rounded to the whole milliseconds both thresholds are expressed in (the
    /// Mac rounds because `Date` arithmetic does not round-trip; kept so the boundaries agree).
    private static double ElapsedMs(DateTimeOffset start, DateTimeOffset end) =>
        Math.Round((end - start).TotalMilliseconds, MidpointRounding.AwayFromZero);

    public bool IsLatched => state is State.Latched;

    /// Whether a key event should still be interpreted at all.
    public bool IsActive => state is not State.Idle;

    public IReadOnlyList<Outcome> Handle(HotkeyAction action, DateTimeOffset now)
    {
        switch (state, action)
        {
            case (State.Idle, HotkeyAction.Press):
                state = new State.Recording(now);
                return [Outcome.StartDictation];

            case (State.Recording r, HotkeyAction.Release):
                // The boundary is `DictationMachine.MinimumMs`, referenced rather than repeated: "too
                // short to be a dictation" and "short enough to be a tap" must never drift apart.
                if (ElapsedMs(r.PressedAt, now) >= DictationMachine.MinimumMs)
                {
                    state = new State.Idle();
                    return [Outcome.EndSession];
                }
                state = new State.WindowOpen(now);
                return [Outcome.HoldOpen];

            case (State.WindowOpen w, HotkeyAction.Press):
                if (ElapsedMs(w.ReleasedAt, now) <= WindowMs)
                {
                    state = new State.Latched();
                    return [Outcome.Latch];
                }
                // Past the deadline. Normally the timer has already fired; when driven manually, bin
                // the stray tap and treat this press as a fresh dictation.
                state = new State.Recording(now);
                return [Outcome.Abandon, Outcome.StartDictation];

            case (State.Latched, HotkeyAction.Press):
                // Ends on key-down, not on release: waiting for the release would make stopping feel laggy.
                state = new State.SwallowingRelease();
                return [Outcome.EndSession];

            // The release of the press that latched, and of the press that ended a latched session.
            case (State.Latched, HotkeyAction.Release):
                return [];
            case (State.SwallowingRelease, HotkeyAction.Release):
                state = new State.Idle();
                return [];

            case (_, HotkeyAction.Cancel):
                // Esc, or a shortcut chord during a hold. The reducer discards the dictation; this
                // type only has to forget the gesture.
                state = new State.Idle();
                return [];

            default:
                return [];
        }
    }

    /// Forgets the current gesture, for endings that do not come through a key event (the 90 s cap, a
    /// recorder that failed to start).
    public void Reset() => state = new State.Idle();

    /// Enters the latched state without a gesture, for a session started from the bar's mic button,
    /// so the key can end what the mouse started.
    public void ForceLatched() => state = new State.Latched();

    /// Called by the coordinator when the window timer fires.
    public IReadOnlyList<Outcome> WindowExpired(DateTimeOffset now)
    {
        if (state is not State.WindowOpen w || ElapsedMs(w.ReleasedAt, now) < WindowMs) return [];
        state = new State.Idle();
        return [Outcome.Abandon];
    }
}

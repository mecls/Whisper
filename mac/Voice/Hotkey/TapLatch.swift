import Foundation

/**
 Decides whether a key gesture is a hold, a double-tap that latches, or a stray tap.

 Pure, and deliberately clock-free: every method takes the time as a parameter and `Coordinator`
 supplies `Date()`. This is the same split already used for the release→paste measurement, where
 `pendingRelease`/`releaseAt` live on the Coordinator so `DictationMachine`'s effects stay
 assertable. A type that reads the clock itself can only be tested with sleeps, and a gesture test
 that sleeps is both slow and flaky.

 The trick that makes latching free is in `handle(.release)`. A press shorter than
 `DictationMachine.minimumMs` is *already* thrown away by the reducer as too short to be a
 dictation, so holding its stop open for a moment costs nothing that existed — while a real hold
 stops the instant the key comes up, with no window and no added latency.
 */
struct TapLatch {
    /// How long after a short release a second press still counts as a double-tap.
    static let windowMs = 300

    enum Outcome: Equatable {
        /// Begin recording. The dictation is real from this moment, even if it later turns out to
        /// have been a stray tap.
        case startDictation
        /// The key came up too quickly to be a dictation. Keep recording and start a timer for
        /// `TapLatch.windowMs`, calling `windowExpired(at:)` if it fires.
        ///
        /// Deliberately carries no deadline. An absolute `Date` in an `Equatable` payload cannot be
        /// asserted on: `t0 + 0.450` and `(t0 + 0.150) + 0.300` print identically and compare
        /// unequal. The caller knows `windowMs`, which is all it needs to schedule the timer.
        case holdOpen
        /// A second press landed inside the window. The recording that is already running becomes
        /// hands-free; nothing is restarted, so audio from the first tap is kept.
        case latch
        /// Stop recording and transcribe. Covers both a normal hold-release and the press that ends
        /// a latched session — from everything downstream they are the same event.
        case endSession
        /// The window closed with no second press. Discard; this was a stray key brush.
        case abandon
    }

    private enum State: Equatable {
        case idle
        case recording(pressedAt: Date)
        case windowOpen(releasedAt: Date)
        case latched
        /// The press that ended a latched session has been handled; its release must be swallowed
        /// so it does not read as the start of something new.
        case swallowingRelease
    }

    private var state: State = .idle

    /// Elapsed milliseconds, rounded to the granularity the decision is actually made at.
    ///
    /// `Date` arithmetic does not round-trip: `Date(1_000_000).addingTimeInterval(0.4)` minus
    /// `Date(1_000_000)` is 399.9999998 ms, not 400, so an exactly-400 ms press read as a tap and an
    /// exactly-300 ms double-tap could fail to latch. Both thresholds are expressed in whole
    /// milliseconds, so compare in whole milliseconds.
    private static func elapsedMs(from start: Date, to end: Date) -> Double {
        (end.timeIntervalSince(start) * 1000).rounded()
    }

    var isLatched: Bool { state == .latched }

    /// Whether a key event should still be interpreted at all. Used by the monitor to decide
    /// between the idle and listening event masks.
    var isActive: Bool { state != .idle }

    mutating func handle(_ action: HotkeyAction, at now: Date) -> [Outcome] {
        switch (state, action) {

        case (.idle, .press):
            state = .recording(pressedAt: now)
            return [.startDictation]

        case (.recording(let pressedAt), .release):
            let heldMs = Self.elapsedMs(from: pressedAt, to: now)
            // The boundary is `DictationMachine.minimumMs`, referenced rather than repeated:
            // "too short to be a dictation" and "short enough to be a tap" are the same threshold
            // and must never drift apart.
            if heldMs >= Double(DictationMachine.minimumMs) {
                state = .idle
                return [.endSession]
            }
            state = .windowOpen(releasedAt: now)
            return [.holdOpen]

        case (.windowOpen(let releasedAt), .press):
            if Self.elapsedMs(from: releasedAt, to: now) <= Double(Self.windowMs) {
                state = .latched
                return [.latch]
            }
            // Past the deadline. In practice the timer has already fired and moved us to `.idle`,
            // so this is only reachable when the window is driven manually — but it must still do
            // the right thing: bin the stray tap and treat this press as a fresh dictation.
            state = .recording(pressedAt: now)
            return [.abandon, .startDictation]

        case (.latched, .press):
            // Ends on key-down, not on release: waiting for the release would make stopping feel
            // laggy for no benefit.
            state = .swallowingRelease
            return [.endSession]

        // The release of the press that latched, and the release of the press that ended a latched
        // session. Both are physical key-ups with no meaning of their own.
        case (.latched, .release), (.swallowingRelease, .release):
            if case .swallowingRelease = state { state = .idle }
            return []

        case (_, .cancel):
            // Esc, or a shortcut chord during a hold. The dictation is discarded by the reducer;
            // all this type has to do is forget the gesture.
            state = .idle
            return []

        default:
            return []
        }
    }

    /// Called by `Coordinator` when the window timer fires.
    mutating func windowExpired(at now: Date) -> [Outcome] {
        guard case .windowOpen(let releasedAt) = state,
              Self.elapsedMs(from: releasedAt, to: now) >= Double(Self.windowMs) else { return [] }
        state = .idle
        return [.abandon]
    }
}

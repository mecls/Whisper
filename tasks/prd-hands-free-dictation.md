# Hands-free dictation — double-tap latch and a persistent bar

## 1. Objective

Dictating today requires holding the key down for the whole utterance. For anything longer than a
sentence that is genuinely uncomfortable, and it rules out dictating while doing something else with
your hands. This adds a **latched mode**: double-tap the dictation key and recording stays on until
you tap again.

It also gives the app a face at the bottom of the screen. Voice is currently invisible between
dictations — the HUD appears 150 ms after a key press and hides 1.2 s after finishing, so there is
no moment at which the app is simply *there*. A **persistent bar** replaces that appear/disappear
panel: always on screen, showing what mode you are in, whether it is listening, and a live waveform
proving the microphone is actually hearing you. That last part is not decoration; an afternoon was
lost to a dictation that silently recorded nothing because AirPods changed the input sample rate,
and a visible flat waveform would have shown it in one second.

Both changes are in the same spec because they are the same feature from the user's side: the bar is
where latched state is visible, and clicking the bar is the second way to start a latched session.

## 2. Business rules (invariants — never violate)

### The latch gesture

1. **A press lasting ≥ `DictationMachine.minimumMs` (400 ms) is a hold, and its release stops the
   dictation immediately — no window, no delay.** This is the existing path and it must stay exactly
   as fast as it is today. The whole sub-second-dictation effort was about release→paste latency;
   adding even 200 ms of "is a second tap coming?" to every normal dictation would spend all of it.

2. **A press lasting < 400 ms is a tap. Its release does not stop the recording.** Instead the
   recording continues and a **300 ms latch window** opens, measured from the release.

   The reason this costs nothing is that a press shorter than `minimumMs` is *already* discarded
   today — `DictationMachine.audioStopped` throws away anything under 400 ms as "Nothing heard". A
   tap was never going to become a dictation, so deferring its stop loses nothing that existed.

3. **A second press inside the 300 ms window latches the session.** The recording that started on
   the first tap continues uninterrupted — the audio buffer is never drained, so anything said
   during the gesture is kept rather than clipped.

4. **The window expiring with no second press discards the recording and shows
   `Strings.tapTooShort`.** It must issue `.cancelRequested` (which drains the buffer and hides the
   HUD), *not* `.hotkeyUp`. If it sent `.hotkeyUp`, the recorder's elapsed time would be the press
   duration plus the 300 ms window — a 200 ms tap would measure 500 ms, clear the 400 ms minimum,
   and turn a stray key brush into a real dictation sent to the server. Follow the pattern already
   used for the no-microphone case in `Coordinator.perform`: show the message, then cancel.

5. **While latched, the next press ends the session immediately, on key-down, not on release.**
   Waiting for the release would make stopping feel laggy for no benefit. The release that follows
   is swallowed: it must not start a new dictation or stop anything.

6. **While latched, a `keyDown` of any other key must NOT cancel the dictation.** Today
   `HotkeyInterpreter.handle` returns `.cancel` for any `keyDown` while the hotkey is held, because
   during a hold that keystroke can only be a shortcut like Fn+←. Latched mode is the opposite
   situation: the user's hands are free and they are expected to keep using the keyboard. Leaving
   this in place would kill the session on the first keystroke, which is precisely the thing latched
   mode exists to allow. `HotkeyMonitor.setListening` should keep the idle mask (flagsChanged only)
   for the duration of a latched session rather than switching to `listeningMask`.

7. **The latch threshold and window are one constant each, named and shared.** Tap threshold reuses
   `DictationMachine.minimumMs`; "too short to be a dictation" and "short enough to be a tap" are the
   same boundary and must never drift apart. The window is a new `TapLatch.windowMs = 300`.

8. **The reducer never reads the wall clock.** `DictationMachine` is a pure reducer and
   `HotkeyInterpreter` is documented as "Pure so the rules are testable"; both must stay that way.
   Timing lives in a new pure `TapLatch` struct whose methods take timestamps as parameters, with
   `Coordinator` supplying `Date()` — the same split already used for the release→paste clock, where
   `pendingRelease`/`releaseAt` were deliberately moved out of the reducer to keep `Effect`
   assertions deterministic.

9. **Latching is available on every `HotkeyChoice`, but double-tap on `.fn` collides with macOS.**
   macOS binds "press 🌐 twice" to its own Dictation by default (symbolic hotkey 164). On this
   machine it happens to be disabled (`enabled = 0`) and the configured key is `rightOption`, so
   there is no conflict today — but a teammate on the `.fn` default would get macOS's dictation panel
   instead. This must be a check in `GO_LIVE.md`, not an assumption.

### The bar

10. **The bar is always visible: idle, listening, transcribing and cleaning.** It replaces
    `Coordinator.showHUD`'s auto-hide entirely. The 150 ms reveal delay for `.listening` and the
    1.2 s hide for `.done`/`.message` stop applying to the panel's *visibility*; they still apply to
    which **content** it shows, so a `.done` state still reverts to idle after 1.2 s.

11. **Clicking the bar must never change the frontmost application.** This is the invariant the
    whole bar rests on. `HUDPanel` is a `.nonactivatingPanel` with `canBecomeKey` and `canBecomeMain`
    both false, and that must not change. If a click activated Voice, the dictation's captured
    frontmost app would become Voice itself, and `TextInjector.mustUseClipboard` would correctly
    route the result to the clipboard instead of pasting it — the text would silently go nowhere the
    user was looking. A test must assert that `FrontmostContext.current()` is unchanged across a
    simulated bar click.

12. **`ignoresMouseEvents` becomes false, and every non-interactive part of the bar is
    `.allowsHitTesting(false)`.** The flag is currently `true` specifically so the panel can never
    swallow a click meant for the window underneath. Making the bar clickable gives that protection
    up, so it has to be reinstated per-view: only the mic button and the mode/language controls are
    hit-testable; the waveform, status text and background are not.

13. **Clicking the mic starts a latched session, and clicking it again ends it.** Mouse-initiated
    sessions are always latched — there is no mouse equivalent of hold-to-talk, and requiring a
    held mouse button would be worse than the key it is meant to replace.

14. **The bar shows a distinct latched indicator, and so does the menu bar icon.** `VoiceApp.menuIcon`
    currently resolves to `mic.badge.xmark` when unauthorized, `mic.fill` while `.listening`, `mic`
    otherwise; latched adds `record.circle.fill`, ranking below the unauthorized badge. A session
    left running by accident must be obvious from the menu bar alone, because the bar can be behind
    a full-screen app.

15. **The bar never shows transcript text unless `Preferences.showTextInHUD` is on.** The existing
    `.done(preview:)` rule carries over unchanged. A persistent bar makes this stricter, not looser:
    an auto-hiding panel showed a preview for 1.2 s, a permanent one would leave the last thing you
    dictated on screen indefinitely.

16. **The bar is suppressible.** New `Preferences.Key.showBar`, default `true`, with a menu bar
    toggle. Permanent screen furniture that cannot be turned off is a bug, and there is no way to
    know in advance whether a persistent bar is welcome on a 13" laptop screen.

17. **The 90 s ceiling still applies and must be visible when it fires.** `AudioRecorder.maxSeconds`
    is 90 and `onCapReached` already sends `.hotkeyUp`. In latched mode this will now actually be
    reached, where in hold-to-talk nobody holds a key for 90 seconds. When it does, the session ends
    and transcribes normally — it must not look like a crash — and the bar shows
    `Strings.latchCapReached` for the usual 1.2 s.

## 3. Flows

**Double-tap to latch**
1. Key down → `.press` → `.hotkeyDown` → `.startRecording`, bar shows Listening. Unchanged.
2. Key up at < 400 ms → `TapLatch` opens a 300 ms window. **No `.hotkeyUp` is sent.** Recording
   continues; the bar stays in Listening.
3. Second key down inside the window → latched. `Coordinator` marks the session latched, tells
   `HotkeyMonitor` to stay on the idle mask (rule 6), and the bar adds the latched indicator.
4. The key-up from that second press is swallowed.
5. Next key down (any time later) → `.hotkeyUp` → `.stopRecording` → the normal transcribe → refine →
   insert pipeline. The following key-up is swallowed.

**Window expires (a stray tap)**
2a. 300 ms passes with no second press → `Coordinator` shows `Strings.tapTooShort` and sends
   `.cancelRequested`. Buffer drained, nothing transcribed, nothing sent to the server. Net effect
   matches today's behaviour for a quick tap, 300 ms later.

**Hold-to-talk (unchanged)**
Key down → record → key up at ≥ 400 ms → stop → transcribe. No window, no added latency, and the
`keyDown`-cancels-the-dictation rule still applies throughout.

**Click to latch**
1. Click the mic on the bar. The panel does not activate (rule 11), so
   `FrontmostContext.current()` still returns the app the user was in.
2. Same as step 3 above: a latched session starts.
3. Click again, or tap the key, to end it. Both must work — a session started by mouse can be ended
   by keyboard and vice versa.

**Failure paths**
- Microphone unavailable at latch time → the existing `recorder.start()` throw path applies:
  `Strings.noMicrophone`, session does not start, no latched state is entered.
- Input device changes mid-latched-session → `AudioRecorder` rebuilds the capture chain and the
  session continues. Already handled; the bar's waveform is what makes it visible.
- Model still loading → `.hotkeyDown` already returns `.hud(.modelLoading)` and starts nothing;
  latching must not bypass that.

## 4. Surfaces

| Surface | Change |
|---|---|
| **`Voice/Hotkey/TapLatch.swift`** (new) | Pure struct. Takes `(action, timestamp)` and returns `.startDictation`, `.holdOpen`, `.latch`, `.endSession`, `.abandon`, or nil. No clock of its own (rule 8). |
| **`HotkeyMonitor`** | Stays on `idleMask` while latched (rule 6) instead of `listeningMask`. |
| **`Coordinator`** | Owns the latch window timer and `Date()`; new `isLatched` state; swallows the releases named in rules 5 and 4. |
| **`DictationMachine`** | No new events if the Coordinator can express latching with the existing `.hotkeyDown`/`.hotkeyUp`/`.cancelRequested`. Prefer that — the reducer's event set is deliberately small. |
| **`HUDPanel`** | Always visible; `ignoresMouseEvents = false`; repositions on `NSApplication.didChangeScreenParametersNotification`. Keeps `.nonactivatingPanel`, `canBecomeKey = false`, `.canJoinAllSpaces`, `.fullScreenAuxiliary`, `level = .floating`. |
| **`HUDView`** | Redesigned as the bar: idle ~220×44, active ~360×64, anchored bottom-centre at `visibleFrame.minY + 40`. Idle shows mic button, mode and language; active adds waveform and status text; latched adds its indicator. |
| **`VoiceApp`** | `menuIcon` gains the latched symbol (rule 14); menu gains a "Show bar" toggle (rule 16). |
| **`Preferences`** | New key `showBar`, default `true`. |
| **`Strings`** | `tapTooShort`, `latched`, `latchCapReached`, `showBar`. |
| **`GO_LIVE.md`** | Check that double-tap on the `.fn` hotkey does not trigger macOS Dictation (rule 9). |

## 5. Validation

`TapLatch` is pure, so the gesture table is a unit test — this is the part most likely to be wrong
and the part cheapest to prove:

| Gesture | Expected |
|---|---|
| down, up after 600 ms | dictation stops on release; never latches |
| down, up after 150 ms, down after 200 ms | latched |
| down, up after 150 ms, down after 400 ms | **not** latched — window expired, first tap abandoned, second starts a fresh dictation |
| down, up after 150 ms, nothing | abandoned at 300 ms; `.cancelRequested`, no transcription |
| latched, then down | session ends on the key-down; the following up is swallowed |
| latched, then any other keyDown | session continues (rule 6) |

```bash
cd mac && xcodegen generate && xcodebuild test -project Voice.xcodeproj -scheme Voice \
  -destination 'platform=macOS,arch=arm64'
# currently: 89 tests, 1 skipped, 0 failures → must be 89 + new tests, 1 skipped, 0 failures
```

Non-automatable, for `GO_LIVE.md` §3 — each needs a microphone, a TCC grant or eyes on the screen:

1. Double-tap, speak for 30 s without touching the keyboard, tap to stop: the full 30 s is
   transcribed and pasted.
2. During that session, type in another app: the session does **not** end (rule 6).
3. Double-tap, then click the bar's mic to stop: works (rule 13's cross-modality).
4. Click the bar's mic while a text field in another app is focused, then dictate: the text lands in
   that field, not on the clipboard — proving the panel never activated (rule 11).
5. Single quick tap: nothing is transcribed, nothing reaches `GET /v1/dictations`, bar shows the
   too-short message.
6. Latched session left running past 90 s: ends cleanly and transcribes (rule 17).
7. Bar is visible over a full-screen app and on a second display.
8. Turn "Show bar" off: the bar disappears and dictation still works entirely from the keyboard.

## 6. Out of scope

- **Pause/resume inside a latched session.** A session runs until it ends. Explicitly excluded.
- **Raising the 90 s audio cap.** `RingBuffer` stays at `sampleRate * maxSeconds`. Longer sessions
  need a chunked capture path feeding the transcriber incrementally, which is a much larger change
  than a hotkey and a panel.
- **Live/streaming transcription — moved, not excluded.** Specified separately in
  `tasks/prd-live-transcription.md`. It was listed here as out of scope on the assumption that it
  meant building streaming ASR; it does not — WhisperKit 0.18.0 already ships
  `AudioStreamTranscriber`. It depends on the bar existing (that is where the live text goes), so
  build this spec first. Nothing in this spec should anticipate it.
- **A separate latch hotkey.** Latching is the double-tap of the existing key plus the bar's mic
  button; no second configurable shortcut.
- **Dragging the bar to a custom position.** Bottom-centre only, for now.

## 7. Open questions

1. **Should Esc cancel a latched session?** Rule 6 removes the blanket keyDown-cancels rule, which
   also removes Esc — today the only way to abandon a dictation without transcribing it. The
   alternative is to keep Esc alone as a special case. I would keep Esc: "throw this away" has no
   other expression, and a 30-second latched session is exactly when you are most likely to want it.
   **Decide before build** — it changes rule 6.
2. **Multi-monitor placement.** The bar currently positions on whichever screen contains the mouse,
   each time it appears. Always-visible makes that a live question: follow the mouse (jumpy), stay on
   the main screen (wrong on a laptop-plus-display setup), or one bar per screen. Proposed: main
   screen, repositioned on screen-parameter changes, revisit after living with it.
3. **Exact bar dimensions.** 220×44 idle and 360×64 active are starting points chosen to sit near the
   current 320×64 panel, not measured against anything. Expect to tune once on screen.
4. **Should the bar auto-hide over full-screen video?** Always-on-top is right for dictating and
   wrong for watching something. No proposal; may not be worth solving.

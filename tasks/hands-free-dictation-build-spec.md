# Miraside Voice — Hands-free dictation — Build Spec

**One-line mission:** Double-tap the dictation key to latch recording on until you tap again, replace
the app's appear-and-vanish HUD with a persistent bar at the bottom of the screen, and stream the
transcript into that bar as the user speaks — which also removes the ASR pass from the release→paste
critical path.

---

## 0. How to use this document

You are building this unattended. Nobody will answer questions while you work, so:

1. **This document outranks your instincts.** Where it names a rule, a number, or an API, follow it
   even if you would have chosen differently.
2. **When you hit something genuinely unspecified, decide and keep moving.** Pick the smallest choice
   consistent with §1 and §2, append it to the Decisions Log in §16 with one line of reasoning, and
   continue. Do not stall and do not invent scope.
3. **§13 is your finish line.** Check your work against those scenarios rather than waiting for a
   human to confirm.
4. **The non-goals in §2 are binding.** If a change would be genuinely useful but sits outside them,
   note it in §16 as a suggestion and do not build it.

The source of truth for *why* each rule exists is `tasks/prd-hands-free-dictation.md`, and the
ordered plan is `tasks/tasks-hands-free-dictation.md`. This document is the executable form of both;
where they disagree, this one wins, and note the disagreement in §16.

Six environment facts that will otherwise waste your time:

- **Build with `./scripts/package.sh`, never bare `xcodebuild`, whenever you intend to *run* the
  app.** They write to different directories — `mac/build/Build/Products/Release/` and
  `~/Library/Developer/Xcode/DerivedData/Voice-*/Build/Products/` — and whichever was launched last
  is what is on screen. Testing a change against the other bundle has already cost this project two
  separate afternoons. `xcodebuild test` is fine for running the suite.
- **Run `xcodegen generate` after adding any file.** Sources are globbed from the directory, so a new
  `.swift` file is invisible to the build until you regenerate. `package.sh` does this for you.
- **The test baseline is 89 tests, 1 skipped, 0 failures.** The skip is the opt-in ASR test
  (`WhisperKitTranscriberTests`); leave it skipped.
- **You cannot verify anything needing a microphone, a TCC grant, or eyes on a screen.** The Mac test
  host does not start the OS adapters at all (`AppDelegate` skips `Coordinator.start()` when
  `XCTestCase` is present, because `AVAudioEngine.inputNode` otherwise blocks on the microphone
  permission gate and the runner times out with zero tests executed). Anything in that category
  belongs in §15's handoff checklist, not in your definition of done.
- **`GO_LIVE.md` is gitignored on purpose** — it carries host and SSH details and the GitHub repo is
  public. You will edit it (§15) and it will correctly not appear in `git status`. Never `git add -f`
  it.
- **The app is now signed with a real identity** (`Apple Development`, team `FZC6P6XRGD`, manual
  signing). Do not change anything under `CODE_SIGN_*` or `DEVELOPMENT_TEAM` in `mac/project.yml`.
  Reverting to ad-hoc would silently drop the Microphone / Input Monitoring / Accessibility grants on
  every build.

`mac/scripts/setinput.swift` switches the default audio input device from the command line if you
need to exercise a route change.

## 1. Primary user and outcome

**Primary user:** Miguel — one person dictating into his own Mac all day, who currently has to hold a
key down for the entire duration of every utterance.

**Outcome:** Start dictating hands-free with two taps, keep using the keyboard and mouse while it
records, and stop with one more tap. Plus: always be able to see, without pressing anything, whether
Voice is listening and whether it is actually hearing you.

**Narrowing principle:** **This changes how a dictation is started and stopped, and how its state is
displayed. It does not change what happens in between.** Audio capture, transcription, cleanup,
injection and the server contract are all untouched. Every feature request that arrives mid-build —
streaming text, editing in the bar, history, a second hotkey — fails this test.

## 2. Non-goals

- **Typing live text progressively into the target app.** Streaming is in scope (step 7), but the
  text it produces goes to Voice's own bar only. Whisper revises unconfirmed segments as it goes and
  a paste cannot be taken back.
- **Server-side ASR.** The VPS is 2 vCPU with no GPU and runs no ASR at all. Transcription stays on
  the Mac's Neural Engine. Do not add an endpoint.
- **Pause/resume inside a latched session.** A session runs until it ends.
- **Raising the 90 s audio cap.** `RingBuffer` stays at `AudioRecorder.sampleRate * maxSeconds`.
  Longer sessions need chunked capture feeding the transcriber incrementally — a much larger change.
- **A separate latch hotkey.** Latching is the double-tap of the existing key plus the bar's mic
  button. No second configurable shortcut, no menu item that starts a session.
- **Dragging the bar to a custom position.** Bottom-centre only.
- **Any change to the server, the API, or the dictation payload.** This feature adds no endpoint and
  no column. `/v1/refine`, `/v1/dictations` and `/v1/insights` are untouched.
- **Any change to signing, the app icon, or the Insights window.**

## 3. Journeys

### Journey A — Double-tap and talk (happy path)
1. User taps the dictation key twice, quickly.
2. Recording starts on the first tap and never stops; the bar shows Listening with a live waveform
   and a latched indicator; the menu bar icon changes to the latched symbol.
3. User speaks for 30 seconds, typing and clicking in other apps throughout. The session does not end.
4. User taps the key once. Recording stops on the key-down. The normal transcribe → refine → insert
   pipeline runs and the text is pasted at the caret.

### Journey B — A stray tap (unhappy path)
1. User brushes the dictation key once, for under 400 ms, and does not tap again.
2. Recording started and is still running; a 300 ms window is open.
3. The window expires. The recording is discarded, the bar shows the too-short message for 1.2 s and
   returns to idle. **Nothing is transcribed and nothing reaches the server.**

### Journey C — Click to dictate (happy path, mouse)
1. User is typing in Mail. They click the mic on the bar.
2. Voice does **not** become the frontmost app. The dictation's captured target app is still Mail.
3. A latched session starts, identical to Journey A step 2.
4. User taps the dictation key to stop — a session started with the mouse can be ended with the
   keyboard, and vice versa. Text pastes into Mail.

### Journey D — The ceiling (unhappy path)
1. A latched session is left running and reaches 90 seconds of audio.
2. `AudioRecorder.onCapReached` fires. The session ends and transcribes normally.
3. The bar shows the cap message for 1.2 s. It must read as a limit, not a crash.

## 4. Screens and states

### The bar (`HUDPanel` + `HUDView`)

- **Primary action:** the mic button — starts a latched session, ends a running one.
- **Secondary actions:** mode (Clean/Literal) and language (auto/pt/en) controls, mirroring the menu
  bar pickers.
- **Placement:** bottom-centre, anchored at `visibleFrame.minY + 40` so it clears the Dock, on the
  screen containing the frontmost window (§16). Repositions on
  `NSApplication.didChangeScreenParametersNotification` and on app activation.
- **Size:** approximately 220×44 idle, 360×64 active. These are starting points, not measurements —
  tune them once and record what you chose in §16.

| State | What the bar shows |
|---|---|
| **Idle** (the resting state — this is what ships and what is on screen 99% of the time) | Mic button, current mode, current language. No waveform, no status text. |
| **Loading** (`modelLoading`) | `Model loading NN %`. The mic button is disabled — a dictation cannot start yet, and `DictationMachine.hotkeyDown` already refuses. |
| **Listening** | Live waveform from `EnergyGate.rms`, status text, plus the latched indicator when latched. |
| **Working** (`transcribing`, `cleaning`) | Status text, with the transcribing percentage when there is one. No waveform. |
| **Success** (`done`) | Checkmark and, only when `Preferences.showTextInHUD` is on, the preview. Reverts to idle after 1.2 s. |
| **Error / notice** (`message`) | The message text. Reverts to idle after 1.2 s. Never a dialog. |
| **Hidden** | When `Preferences.showBar` is false the panel is ordered out entirely and dictation works from the keyboard exactly as before. |

There is no empty state distinct from Idle: the bar has no content that can be missing.

## 5. Capabilities

- [ ] `TapLatch` — pure gesture type: hold vs tap, 300 ms window, latch, end, abandon
- [ ] Double-tap latches a session; a single tap within the window does not
- [ ] A latched session survives every other keystroke except Esc
- [ ] Esc cancels and discards a latched session
- [ ] The 90 s cap ends a latched session cleanly and visibly
- [ ] The HUD panel becomes always-visible
- [ ] The panel is clickable without activating the app
- [ ] Bar idle layout: mic, mode, language
- [ ] Bar active layout: waveform, status, latched indicator
- [ ] Click the mic to start and end a latched session
- [ ] `Preferences.showBar` and a menu bar toggle
- [ ] Latched symbol in the menu bar icon
- [ ] `VoiceAudioProcessor` — four-member `AudioProcessing` adapter over the existing `AudioRecorder`
- [ ] Live transcript streaming into the bar, confirmed plain and unconfirmed dimmed
- [ ] The streamed result becomes the pasted text, with a one-pass fallback when it yields nothing
- [ ] Single-segment-only skip gate rule for streamed transcripts

## 6. Invariants — enforce in the client

*The template calls this section "enforce on the server". This feature has no server surface at all —
it adds no endpoint, no column and no payload field — so every invariant below is enforced in the Mac
client, and each one names the type that owns it. Do not add a server component.*

1. **A press lasting ≥ `DictationMachine.minimumMs` (400 ms) is a hold; its release stops the
   dictation immediately, with no window and no added delay.** The sub-second-dictation work exists
   to minimise release→paste latency; adding a "is a second tap coming?" wait to every normal
   dictation would spend all of it. Owner: `TapLatch`.
2. **A press under 400 ms does not stop the recording on release.** It opens a 300 ms window with the
   recording still running. This is free because a press that short is *already* discarded by
   `DictationMachine.audioStopped` — it was never going to become a dictation. Owner: `TapLatch`.
3. **The tap threshold is `DictationMachine.minimumMs`, referenced, not re-declared.** "Too short to
   be a dictation" and "short enough to be a tap" are the same boundary and must never drift apart.
   The window is `TapLatch.windowMs = 300`.
4. **A window that expires with no second press issues `.cancelRequested`, never `.hotkeyUp`.**
   `.hotkeyUp` would measure the press duration *plus* the 300 ms window — a 200 ms brush would
   measure 500 ms, clear the 400 ms minimum, and turn a stray keypress into a real dictation sent to
   the server. Show `Strings.tapTooShort` first, then cancel, following the `noMicrophone` path
   already in `Coordinator.perform`'s `.startRecording` case. Owner: `Coordinator`.
5. **While latched, the next press ends the session on key-**down**, and the release that follows is
   swallowed.** The swallowed release must not start a new dictation. Owner: `TapLatch`.
6. **While latched, no key cancels the dictation except Esc — matched on the existing
   `HotkeyInterpreter.escapeKeyCode`, not a literal `53`.** Today
   `HotkeyInterpreter.handle` returns `.cancel` for *any* `keyDown` while the key is held, which is
   right for a hold — that keystroke can only be a shortcut. It is exactly wrong for latched mode,
   whose entire purpose is to let you keep using the keyboard; the session would die on the first
   keystroke. Esc survives as the single exception because "throw this away" has no other expression
   (§16). Owner: `HotkeyMonitor` / `HotkeyInterpreter`.
7. **Neither `DictationMachine` nor `HotkeyInterpreter` may read the wall clock.** Both are
   documented as pure and their tests depend on it. `TapLatch` takes timestamps as parameters;
   `Coordinator` supplies `Date()`. This mirrors the existing split where `pendingRelease`/`releaseAt`
   were deliberately moved out of the reducer to keep `Effect` assertions deterministic.
8. **Clicking the bar must never change the frontmost application.** `HUDPanel` must keep
   `.nonactivatingPanel`, `canBecomeKey == false` and `canBecomeMain == false`. If a click activated
   Voice, the dictation's captured app would become Voice itself and
   `TextInjector.mustUseClipboard` would correctly divert the text to the clipboard — it would
   silently not appear where the user was looking. Owner: `HUDPanel`.
9. **The bar must not swallow clicks meant for the app underneath.** `ignoresMouseEvents` is
   currently `true` precisely to guarantee this, and making the bar clickable gives that up. Reinstate
   it by construction: see §11 step 2 for the decision tree, and size the panel to its controls rather
   than using a wide transparent container. Owner: `HUDPanel`.
10. **The bar never shows transcript text unless `Preferences.showTextInHUD` is on.** A panel that
    auto-hid showed a preview for 1.2 s; a permanent one would otherwise leave the last thing you
    dictated on screen indefinitely. Owner: `HUDView`.
11. **No transcript text is written to disk.** Unchanged from today and non-negotiable: the bar holds
    text in memory only, and nothing about this feature persists a dictation locally.
12. **`Preferences.showBar` defaults to `true` but must fully suppress the panel when false.**
    Permanent screen furniture that cannot be turned off is a bug. With it off, every keyboard path
    still works. Owner: `HUDPanel`.
13. **The latched indicator appears in the menu bar as well as the bar.** `VoiceApp.menuIcon` ranks:
    `sync.unauthorized` → `mic.badge.xmark`, then latched → `record.circle.fill`, then `.listening` →
    `mic.fill`, else `mic`. The bar can sit behind a full-screen app; the menu bar cannot.
14. **The 90 s ceiling stays at `AudioRecorder.maxSeconds` and is surfaced when it fires.** In
    hold-to-talk nobody holds a key for 90 seconds, so this path has never been reachable; latched
    mode makes it real. Show `Strings.latchCapReached`. Owner: `Coordinator`.
15. **A session started by mouse can be ended by keyboard and vice versa.** There is one session
    concept, not two. Owner: `Coordinator`.

### Streaming (step 7) — the full rule set is `tasks/prd-live-transcription.md` §2

16. **Live text is display-only and never typed into the target app.** The target receives exactly one
    insertion, at the end, as it does today.
17. **`AudioRecorder` is not modified.** Bridge to WhisperKit with a `VoiceAudioProcessor:
    AudioProcessing` adapter. `AudioStreamTranscriber` calls only four of its members —
    `startRecordingLive`, `stopRecording`, `relativeEnergy`, `audioSamples` — verified by reading its
    224 lines. The static file-loading half delegates to WhisperKit's own `AudioProcessor`. Handing
    capture to WhisperKit would re-open the AirPods sample-rate bug fixed on 2026-09-12.
18. **Do not add a purge operation to `RingBuffer`.** Streaming never calls `purgeAudioSamples`, and
    our buffer is already fixed-capacity. **First verify the buffer does not wrap before
    `onCapReached` fires** — `AudioStreamTranscriber` tracks position with `lastBufferSize`, and
    shifting indices would make it silently re-transcribe or skip audio. If it wraps, stop and say so.
19. **The streamed result is the pasted text, in both modes, with no second ASR pass.** Concatenate
    `confirmedSegments` in order and trim once; never include `unconfirmedSegments`.
20. **A streamed transcript may only skip cleanup when it was a single confirmed segment.** One
    segment has no chunk boundary and is safe; two or more go to cleanup however clean they look. The
    skip path is the only one where nothing inspects the text before it reaches the user's document.
21. **If streaming yields nothing usable, fall back to one-pass transcription of the ring buffer.**
    Empty or whitespace-only after trimming counts as nothing usable. A dictation must never be
    silently lost — that exact failure cost this project an afternoon when a 24 kHz AirPods stream
    converted to silence.
22. **`saveSegment` must be `false` everywhere.** `AudioProcessing.padOrTrimAudio` takes a flag that
    writes audio to disk for debugging. The app's stated privacy property is that transcripts never
    touch the Mac's disk, and a library flag left on would break it invisibly.
23. **Live text obeys `Preferences.showTextInHUD`**, and no transcript text reaches the log — the
    per-chunk callback fires many times per dictation.

## 7. Data model and lifecycle

This feature adds **no persistent entities**. There is no database, no migration and no new file on
disk. Two pieces of state exist:

### `Preferences.showBar` (new, durable)
- **Represents:** whether the bar is on screen.
- **Owned by:** `UserDefaults`, via `Preferences.Key.showBar`. Default `true`.
- **Lifecycle:** toggled from the menu bar. Reversible, survives relaunch. Follow the shape of the
  existing `sounds` / `showTextInHUD` entries exactly.

### The latch session (new, transient)
- **Represents:** whether the current dictation is hands-free.
- **Owned by:** `Coordinator.isLatched` (`@Published`), with the gesture state inside `TapLatch`.
- **States:** idle → recording (tap) → *either* latched → ended, *or* abandoned. Never persisted;
  a relaunch always starts idle.
- **Permanent:** nothing. A discarded session leaves no trace, which is the point of Journey B.

The existing `Dictation` record and the server's `dictations` table are untouched. A latched
dictation is indistinguishable from a held one in the payload — deliberately, so the existing
latency and word-count metrics stay comparable across both.

## 8. Users, auth and permissions

**Single user, no roles, no auth surface in this feature.** Voice is a local menu-bar app; the bar and
the latch gesture are available to whoever is at the keyboard. Dictations still reach the server over
the existing bearer-token path, unchanged — do not touch `VoiceAPI`, `Keychain` or `SyncService`.

The OS permissions this depends on (Input Monitoring for the key, Accessibility for the paste,
Microphone for audio) are already granted and are **not** re-requested by this feature. If any is
missing, existing code already handles it: `Coordinator.start` logs a warning and
`recorder.start()` throws `AudioRecorderError.noInputDevice`, surfacing `Strings.noMicrophone`.

## 9. Technical direction

This is an existing codebase. Match it; introduce nothing new.

- **Language:** Swift 6.3 (Xcode 26.6), SwiftUI + AppKit. Deployment target macOS 15.0.
- **Project:** generated by **XcodeGen** from `mac/project.yml`. Re-run `xcodegen generate` after
  adding any file.
- **Tests:** XCTest, flat in `mac/VoiceTests/`, named `<Thing>Tests.swift`. No subfolders.
- **No new dependencies.** The only package is WhisperKit and this feature does not touch it. No
  charting, animation or hotkey library.
- **No server, no database, no migration.**

### External integrations

| Integration | Used for | Notes |
|---|---|---|
| `CGEventTap` (existing `EventTap`) | Seeing the modifier key | Already running. This feature only changes which mask is active while latched. |
| `AVAudioEngine` (existing `AudioRecorder`) | Capture | Untouched. It already rebuilds its chain on device change; do not modify it. |
| `NSPanel` / AppKit | The bar | The one genuinely risky surface — see §11 step 2. |

No network calls are added. The server is live and irrelevant here.

## 10. Security and privacy

- **Sensitive data:** dictation transcripts. They exist in memory and in the server's SQLite, never
  on the Mac's disk, and this feature must not change that (invariant 11).
- **The new exposure is visual, not network.** A permanent bar can display the last dictation's
  preview indefinitely where an auto-hiding one showed it for 1.2 s. Invariant 10 is the guardrail:
  the preview stays behind `Preferences.showTextInHUD`.
- **The bar is visible on every Space and over full-screen apps** (`collectionBehavior` already sets
  `.canJoinAllSpaces` and `.fullScreenAuxiliary`). Anyone screen-sharing will show it. That is
  accepted; it is why invariant 10 matters and why invariant 12 exists.
- **No new logging of transcript text.** Follow the existing convention: log lengths and counts
  (`rawChars`), never content.
- **Input validation:** not applicable — no new input crosses a trust boundary.

## 11. Build order

Each step leaves the repo green. Run the suite before moving on.

1. **`TapLatch` and its tests.** Pure, no UI, no engine. It is the part most likely to be wrong and
   the cheapest to prove, so it comes first — before anything touches a real key. All of §13's
   gesture scenarios pass at the end of this step.

2. **The click-through spike, before any bar UI.** Temporarily set `ignoresMouseEvents = false` on
   `HUDPanel`, add a button, and with another app focused verify two things: that
   `FrontmostContext.current()` is unchanged after a click (invariant 8), and whether a click on a
   transparent, `.allowsHitTesting(false)` region reaches the window underneath (invariant 9).

   **An `NSPanel` still receives the event at the AppKit layer even where SwiftUI declines it, so
   expect the second one to fail.** If it does, the resolution is already decided: **size the panel's
   frame to the visible bar rather than using a wide transparent container**, so the unavoidable dead
   zone is small and exactly where the bar visibly is. Do not implement hover-based
   `ignoresMouseEvents` toggling and do not abandon click-to-dictate. Record what the spike actually
   showed in §16, then revert it.

   If the *first* check fails — a click does change the frontmost app — stop. Invariants 8 and 15 are
   unbuildable as written, and that is a genuine design fork, not something to decide alone. Build
   everything else, leave the mic button non-interactive, and say so in your final message.

3. **Latching wired through `HotkeyMonitor` and `Coordinator`.** Feature-complete from the keyboard,
   still with the old HUD. Includes invariant 6's Esc exception and invariant 14's cap handling.

4. **The panel becomes persistent.** `Coordinator.showHUD` stops hiding; content still reverts after
   1.2 s. No new layout yet — prove persistence separately from redesign.

5. **The bar's layouts and the mic button**, using what step 2 settled.

6. **`Preferences.showBar`, the menu toggle, the menu bar icon, and `Strings`.**

7. **Live transcription.** Only after the bar exists — it is where the text goes. Build the adapter
   first and verify invariant 18's no-wrap condition before wiring the stream. Then
   `StreamingTranscriber`, then the bar's confirmed/unconfirmed rendering, then invariants 19–21.

   **This step has a measured gate, and it is a stop condition.** Run the 20 pt/en fixtures in
   `server/src/cli/bench-fixtures.ts` through both the streamed and one-pass paths and compare word
   error rate. **Ship only if streamed WER is within 2 percentage points of one-pass.** If it is
   worse, do not tune parameters blindly and do not add a second ASR pass — record both numbers in
   `docs/SPIKES.md`, leave step 7 behind a disabled flag so steps 1–6 still ship, and say so in your
   final message. That is a judgement call about output quality reaching the user's documents, and it
   is not yours to make alone.

8. **`GO_LIVE.md`** — append the human-only checks from §15.

## 12. Testing

- **Unit (Mac):**
  ```bash
  cd mac && xcodegen generate && xcodebuild test -project Voice.xcodeproj -scheme Voice \
    -destination 'platform=macOS,arch=arm64'
  ```
  Must go from **89 passing (1 skipped)** to 89 + yours, 0 failures. Leave the skip skipped.
- **Pure types get tests; SwiftUI views do not.** `TapLatch` is the whole point of this rule — put
  every gesture decision in it, not in `Coordinator`, so the table in §13 is a unit test rather than
  something only a human with a keyboard can check.
- **Drive time with explicit `Date` values, never `sleep`.** A gesture test that waits 300 ms in real
  time is both slow and flaky, and `TapLatch` takes timestamps precisely so it does not have to.
- **No end-to-end test.** Everything end-to-end here needs a microphone, a TCC grant and eyes on a
  screen; those are §15's handoff checklist.

## 13. Acceptance scenarios — your finish line

### AC-1 — Double-tap latches (happy path, Journey A)
- **Given** a `TapLatch` in its idle state
- **When** it receives `.press` at t=0, `.release` at t=150 ms, and `.press` at t=350 ms
- **Then** the outcomes are `.startDictation`, `.holdOpen`, `.latch` — and no `.hotkeyUp` is produced
  between the first release and the latch

### AC-2 — A hold never latches and is never delayed
- **Given** the same
- **When** it receives `.press` at t=0 and `.release` at t=600 ms
- **Then** the release returns the outcome that stops the dictation immediately, with no window
  opened and no timer scheduled

### AC-3 — The threshold does not drift
- **Given** the same
- **When** one gesture releases at 399 ms and another at 401 ms
- **Then** the first opens a window and the second stops immediately, and the boundary asserted in
  the test is `DictationMachine.minimumMs` rather than a literal `400`

### AC-4 — A stray tap transcribes nothing (failure path, Journey B)
- **Given** a tap whose window has opened
- **When** 300 ms pass with no second press
- **Then** the outcome is `.abandon`, and the `Coordinator` path it drives issues `.cancelRequested`
  — **not** `.hotkeyUp`. Assert on the `Effect` list from `DictationMachine`: it must contain
  `.discardRecording` and must not contain `.transcribe`

### AC-5 — A latched session survives the keyboard but not Esc (invariant 6)
- **Given** a latched session
- **When** an arbitrary `keyDown` (say `a`) arrives
- **Then** the session continues and no `.cancel` is produced
- **And when** `keyDown HotkeyInterpreter.escapeKeyCode` (Esc) arrives
- **Then** the session is cancelled and discarded

### AC-6 — Ending is idempotent across input methods (invariant 15)
- **Given** a latched session started by the mic button
- **When** the dictation key is pressed
- **Then** the session ends exactly once — the subsequent `.release` is swallowed and produces no
  second stop and no new dictation

### AC-7 — Clicking the bar does not steal the target app (invariant 8)
- **Given** the bar on screen and another application frontmost
- **When** the bar's mic button is clicked
- **Then** `FrontmostContext.current()` returns the same bundle id before and after, and
  `TextInjector.mustUseClipboard(targetBundleId:secureInputActive:)` returns `false` for it
- *(If step 2's spike showed this cannot hold, this scenario is waived — say so in §16 and in your
  final message.)*

### AC-8 — The streamed result is what gets pasted, and nothing is lost when it is empty
- **Given** a `StreamingTranscriber` whose state carries two confirmed segments and one unconfirmed
- **When** the session ends
- **Then** the dictation's `raw` is the two confirmed segments joined in order and trimmed once, and
  the unconfirmed text does not appear in it
- **And given** a session that ends with no confirmed segments
- **Then** the one-pass fallback runs against the ring buffer and the dictation still completes —
  assert the `Effect` list contains `.transcribe`, not a discard

### AC-9 — A boundary-carrying transcript cannot skip cleanup (invariant 20)
- **Given** a streamed transcript of two confirmed segments whose joined text would otherwise satisfy
  every clause of `SkipGate` (short, punctuated, no fillers)
- **When** the skip decision is made
- **Then** it does **not** skip, and the dictation goes to `/v1/refine`
- **And given** the same text arriving as a single confirmed segment
- **Then** it skips exactly as it does today

## 14. Failure recovery

- **Errors surface in the bar and in the existing `Logger`** (`subsystem: "co.miraside.voice"`).
  Nothing is swallowed silently — that is the specific failure that made this month's two worst bugs
  take an afternoon each.
- **A failed `recorder.start()` must not enter latched state.** The existing throw path shows
  `Strings.noMicrophone` and cancels; latching must not bypass it or the user gets a latched
  indicator over a dead engine.
- **An input device change mid-session is already handled** by `AudioRecorder` and the session
  continues. Do not add a second recovery path.
- **A window timer must be cancellable and must not fire after the session it belongs to has ended.**
  Use `DispatchWorkItem` and follow the existing `hudShowWork` / `hideWork` pattern in
  `Coordinator.showHUD` rather than `Timer`.
- **Never do this:** leave `isLatched` true after a session ends by any route — cap, cancel, Esc,
  insert, or a failed start. A stale latched indicator is worse than none, because it is the one
  thing telling the user a microphone is live.

## 15. Deliverables

- [ ] Working feature on a branch (§16)
- [ ] `mac/Voice/Hotkey/TapLatch.swift` and `mac/VoiceTests/TapLatchTests.swift`
- [ ] All tests passing, counts reported in your final message against the 89 baseline
- [ ] A Release build via `./scripts/package.sh` that succeeds and is signed with
      `TeamIdentifier=FZC6P6XRGD` (the script prints it — if it prints `adhoc`, something reverted
      the signing config and that is a bug, not a warning)
- [ ] **Human-only checks appended to `GO_LIVE.md` §3** (gitignored — edit it, never commit it):
      double-tap and speak for 30 s while typing in another app; Esc during a latched session;
      click the bar's mic with a text field focused elsewhere and confirm the text lands there and
      not on the clipboard; a single stray tap transcribes nothing; a session past 90 s ends cleanly;
      the bar over a full-screen app and on a second display; "Show bar" off and dictation still
      works; that live text appears in the bar while speaking and stops appearing with
      `showTextInHUD` off; that a 30 s latched session pastes text matching what the bar showed; and
      — for anyone on the `.fn` hotkey — that double-tap does not open macOS Dictation
      (System Settings › Keyboard › Dictation shortcut). This Mac is on `rightOption` with macOS
      Dictation disabled, so it will not reproduce here.
- [ ] `docs/SPIKES.md` updated with the streamed-vs-one-pass WER numbers from build step 7 — both
      figures, not a verdict
- [ ] The Decisions Log in §16, filled in
- [ ] A final message stating test counts, what step 2's spike actually found, what you could not
      verify, and anything you added to §16

## 16. Decisions log

Work on a branch off `main` named `feat/hands-free-dictation`. **Commit as you go** — one commit per
step in §11, with messages explaining why, not what. **Do not push and do not open a PR.** End commit
messages with:

```
Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

Append one line per decision this spec did not settle, in the form `<what you decided> — <why, in one
clause>`. Also record anything you deliberately did not build because §2 excluded it.

Decisions already made for you, so you do not re-litigate them:

- **Esc cancels a latched session; every other key is ignored.** Invariant 6's exception. "Throw this
  away" has no other expression, and a 30-second hands-free session is exactly when it is wanted.
- **The bar lives on the screen containing the frontmost window**, repositioning on screen-parameter
  changes and app activation. Following the mouse pointer makes an always-visible bar jump between
  displays; main-screen-only is wrong whenever the user is working on the second display.
- **If the click-through spike fails, shrink the panel to its controls.** Not hover-based
  `ignoresMouseEvents` toggling (fiddly, and a missed tracking event leaves the bar unclickable) and
  not abandoning click-to-dictate.
- **Branch and commit, do not push.** The repo is public and the push stays under human control.
- **The latch gesture reuses `DictationMachine.minimumMs`** rather than declaring its own threshold.
- **A latched dictation is indistinguishable from a held one in the server payload**, so existing
  latency and word-count metrics stay comparable.

## 17. Definition of Done

> `cd mac && xcodegen generate && xcodebuild test -project Voice.xcodeproj -scheme Voice
> -destination 'platform=macOS,arch=arm64'` reports **0 failures** with every scenario in §13 covered
> by a named test; `./scripts/package.sh` succeeds and prints `TeamIdentifier=FZC6P6XRGD`; the
> `TapLatch` gesture table passes without any test calling `sleep`; streamed WER is recorded in
> `docs/SPIKES.md` and is either within 2 points of one-pass (step 7 ships) or step 7 is behind a
> disabled flag with the numbers reported; and the human-only checks are written into `GO_LIVE.md` §3.

# Tasks — Hands-free dictation (double-tap latch and a persistent bar)

Source specs: `tasks/prd-hands-free-dictation.md` (tasks 0–5, 7) and
`tasks/prd-live-transcription.md` (task 6). Agent-facing form:
`tasks/hands-free-dictation-build-spec.md`.

## Relevant Files

- `mac/Voice/Hotkey/TapLatch.swift` — **new.** The gesture rules as a pure type: tap vs hold, the
  300 ms window, latch, end, abandon. Takes timestamps as parameters so it never reads a clock
  (spec rule 8).
- `mac/Voice/Hotkey/HotkeyInterpreter.swift` — pure `.press`/`.release`/`.cancel` producer. Stays
  clock-free; the `keyDown → .cancel` rule needs a latched-mode exception (rule 6).
- `mac/Voice/Hotkey/HotkeyMonitor.swift` — owns the tap and swaps `idleMask`/`listeningMask`. Must
  stay on `idleMask` for the whole latched session.
- `mac/Voice/App/Coordinator.swift` — owns the wall clock and the window timer, holds `isLatched`,
  swallows the releases in rules 4 and 5.
- `mac/Voice/App/DictationMachine.swift` — pure reducer; `minimumMs = 400` is the tap threshold the
  spec reuses. Ideally unchanged.
- `mac/Voice/UI/HUDPanel.swift` — the `NSPanel` that becomes the bar: always visible,
  `ignoresMouseEvents = false`, still non-activating.
- `mac/Voice/UI/HUDView.swift` — 320×64 panel today; becomes the bar's idle and active layouts.
- `mac/Voice/App/VoiceApp.swift` — `menuIcon` gains the latched symbol; menu gains the bar toggle.
- `mac/Voice/Storage/Preferences.swift` — new `showBar` key, default `true`.
- `mac/Voice/UI/Strings.swift` — `tapTooShort`, `latched`, `latchCapReached`, `showBar`.
- `mac/Voice/Inject/FrontmostContext.swift` — what rule 11 protects: a bar click must not change
  what this returns.
- `mac/VoiceTests/TapLatchTests.swift` — **new.** The gesture table from spec §5; the highest-value
  tests in this build.
- `mac/VoiceTests/HotkeyInterpreterTests.swift` — existing; extend for the latched `keyDown` case.
- `mac/VoiceTests/DictationMachineTests.swift` — existing; guards the reducer if its events change.
- `mac/Voice/ASR/VoiceAudioProcessor.swift` — **new.** Four-member `AudioProcessing` adapter over
  the existing recorder. `AudioStreamTranscriber` calls only `startRecordingLive`, `stopRecording`,
  `relativeEnergy` and `audioSamples`; the static file-loading half delegates to WhisperKit's own
  `AudioProcessor`.
- `mac/Voice/ASR/StreamingTranscriber.swift` — **new.** Owns the `AudioStreamTranscriber` actor,
  publishes confirmed/unconfirmed text, handles the one-pass fallback.
- `mac/Voice/Refine/SkipGate.swift` — gains the single-segment rule for streamed transcripts.
- `mac/VoiceTests/StreamingTranscriberTests.swift` — **new.** Segment joining, the fallback trigger,
  and the skip-gate interaction. No microphone required.
- `docs/SPIKES.md` — where the streamed-vs-one-pass WER numbers get recorded.
- `GO_LIVE.md` — gitignored, local only. Gains the macOS double-tap conflict check and the
  hands-free items from spec §5.

### Notes

- Mac tests are XCTest, flat in `mac/VoiceTests/`, named `<Thing>Tests.swift`. No `tests/` directory
  and no per-feature subfolders — follow the existing layout.
- Run them with:
  ```bash
  cd mac && xcodegen generate && xcodebuild test -project Voice.xcodeproj -scheme Voice \
    -destination 'platform=macOS,arch=arm64'
  ```
  Baseline is **89 tests, 1 skipped, 0 failures**. The skip is the opt-in ASR test; leave it skipped.
- **`xcodegen generate` after adding any file.** Sources are globbed from the directory, so a new
  `.swift` file is invisible to the build until the project is regenerated.
- **To run the app, build with `./scripts/package.sh`, not bare `xcodebuild`.** They write to
  different places — `mac/build/` and `~/Library/Developer/Xcode/DerivedData/` — and whichever was
  launched last is what is on screen. Testing a fix against the wrong bundle has already cost this
  project an afternoon twice.
- `mac/scripts/setinput.swift` switches the default audio input from the command line; useful if any
  of this needs testing against a device change mid-session.

## Instructions for Completing Tasks

As you complete each sub-task, check it off by changing `- [ ]` to `- [x]`, and save the file
then — not at the end of the parent task. Someone picking this up after an interruption can
only trust the boxes if they were ticked as the work happened.

## Tasks

- [ ] 0.0 Create the feature branch (currently on `main`, with uncommitted work in the tree)
  - [ ] 0.1 Commit the untracked spec and task files in `tasks/` so the tree is clean before branching
  - [ ] 0.2 `git checkout -b feat/hands-free-dictation` from `main`

- [ ] 1.0 Settle the two open decisions and prove the assumption the bar rests on, before writing UI
  - [ ] 1.1 Decide spec §7 Q1 (does Esc cancel a latched session?) and write the answer into rule 6
        of `tasks/prd-hands-free-dictation.md`. This gates task 3.5 — build it after, not before.
  - [ ] 1.2 Decide spec §7 Q2 (multi-monitor placement) and write it into rule 10. Gates task 4.3.
  - [ ] 1.3 **Spike, throwaway:** temporarily set `ignoresMouseEvents = false` in `HUDPanel` and add a
        test button. With TextEdit focused, click it and log `FrontmostContext.current()` before and
        after. Confirm it is unchanged. If it *does* change, rules 11 and 13 are unbuildable as
        written and tasks 4.6–4.7 need redesigning — stop and say so.
  - [ ] 1.4 **Spike, throwaway:** in the same build, put `.allowsHitTesting(false)` on the panel's
        background and confirm a click on that area reaches the window underneath rather than being
        swallowed. An `NSPanel` still receives the event even when SwiftUI declines it, so this may
        need `NSWindow.ignoresMouseEvents` toggled dynamically or a smaller panel frame. This decides
        whether the bar can be full-width or must be only as wide as its controls.
  - [ ] 1.5 Revert both spikes; record what they proved in the spec's §7 so the next reader does not
        repeat them.

- [ ] 2.0 Build `TapLatch` as a pure, fully tested gesture type
  - [ ] 2.1 Create `mac/Voice/Hotkey/TapLatch.swift` with an `Outcome` enum
        (`.startDictation`, `.holdOpen`, `.latch`, `.endSession`, `.abandon`) and a `mutating func
        handle(_ action: HotkeyAction, at: Date) -> Outcome?`. No `Date()` inside the type.
  - [ ] 2.2 Implement the hold/tap split against `DictationMachine.minimumMs` — reference the
        existing constant, do not write `400` a second time (rule 7).
  - [ ] 2.3 Add `static let windowMs = 300` and implement latch-vs-abandon: a `.press` inside the
        window latches, anything later starts a fresh dictation.
  - [ ] 2.4 Implement rule 5: while latched, a `.press` returns `.endSession` and the `.release`
        that follows it returns nil (swallowed, not treated as a new gesture).
  - [ ] 2.5 Create `mac/VoiceTests/TapLatchTests.swift` covering all six rows of the spec §5 gesture
        table, driving time with explicit `Date` values rather than sleeps.
  - [ ] 2.6 Add a test that a 399 ms and a 401 ms press fall on opposite sides of the threshold — the
        boundary is the thing most likely to drift.
  - [ ] 2.7 `xcodegen generate` and run the suite; expect 89 + new tests, 0 failures.

- [ ] 3.0 Wire latched sessions through the monitor and coordinator, keyboard-driven end to end
  - [ ] 3.1 Hold a `TapLatch` in `Coordinator` and feed it from the existing `hotkey.onAction`
        closure, supplying `Date()` there — the Coordinator already owns the wall clock for
        `pendingRelease`/`releaseAt`; follow that precedent.
  - [ ] 3.2 Add a cancellable window timer using `DispatchWorkItem`, following the existing
        `hudShowWork`/`hideWork` pattern in `Coordinator.showHUD` rather than a `Timer`.
  - [ ] 3.3 On `.abandon`, call `showHUD(.message(Strings.tapTooShort))` then `send(.cancelRequested)`
        — the same order as the `noMicrophone` path in `Coordinator.perform`'s `.startRecording` case.
  - [ ] 3.4 Add `@Published private(set) var isLatched` to `Coordinator`, set on `.latch` and cleared
        on `.endSession`, on `.cancelRequested`, and on the `.inserted` that ends the session.
  - [ ] 3.5 Implement rule 6 (as amended by task 1.1): `HotkeyMonitor.setListening` keeps `idleMask`
        while latched so `keyDown` never reaches `HotkeyInterpreter` and cannot cancel.
  - [ ] 3.6 Handle rule 17: `recorder.onCapReached` already sends `.hotkeyUp`; make the latched path
        clear `isLatched` and show `Strings.latchCapReached` so a 90 s cutoff reads as a limit, not a
        crash.
  - [ ] 3.7 Extend `mac/VoiceTests/HotkeyInterpreterTests.swift` for the latched `keyDown` case, and
        `DictationMachineTests.swift` only if the reducer's events actually changed.
  - [ ] 3.8 Run the suite, then build with `./scripts/package.sh` and confirm by hand that
        double-tap latches and a second tap ends it.

- [ ] 4.0 Turn the HUD panel into the persistent, clickable bar
  - [ ] 4.1 In `Coordinator.showHUD`, stop hiding the panel: `.done`/`.message` revert the *content*
        to idle after 1.2 s instead of calling `panel.hide()`, and `.listening` no longer needs the
        150 ms reveal delay (rule 10).
  - [ ] 4.2 In `HUDPanel.init`, set `ignoresMouseEvents = false` (or the dynamic form task 1.4
        settled). Leave `.nonactivatingPanel`, `canBecomeKey`, `canBecomeMain`, `level` and
        `collectionBehavior` exactly as they are — rule 11 depends on every one of them.
  - [ ] 4.3 Reposition on `NSApplication.didChangeScreenParametersNotification` using the placement
        decided in task 1.2, keeping the `visibleFrame.minY + 40` anchor so it clears the Dock.
  - [ ] 4.4 Add an idle layout to `HUDView` (~220×44): mic button, current mode, current language.
  - [ ] 4.5 Extend the active layout (~360×64) with the existing waveform, the stage text, and a
        latched indicator driven by `Coordinator.isLatched`.
  - [ ] 4.6 Apply `.allowsHitTesting(false)` to the background, waveform and status text; leave it on
        only for the mic button and the mode/language controls (rule 12).
  - [ ] 4.7 Wire the mic button to start a latched session and, when one is running, to end it —
        reaching the same Coordinator entry points as the keyboard so either can end the other
        (spec §3, "Click to latch").
  - [ ] 4.8 Keep the `.done(preview:)` text behind `Preferences.showTextInHUD` (rule 15) — on a
        permanent bar this leaves the last dictation on screen indefinitely if it regresses.
  - [ ] 4.9 Run the suite, then `./scripts/package.sh` and look at the bar in both states.

- [ ] 5.0 Settings, menu bar, and copy
  - [ ] 5.1 Add `showBar` to `Preferences.Key` and to `Preferences.defaults` with `true`, plus the
        accessor, following the shape of the existing `sounds`/`showTextInHUD` entries.
  - [ ] 5.2 Add a "Show bar" toggle to the `MenuBarExtra` content in `VoiceApp.swift` and have
        `HUDPanel` order itself out when it is off (rule 16).
  - [ ] 5.3 Extend `VoiceApp.menuIcon` with `record.circle.fill` for latched, ranked below the
        `sync.unauthorized` badge and above `.listening` (rule 14).
  - [ ] 5.4 Add `tapTooShort`, `latched`, `latchCapReached` and `showBar` to `Strings.swift`.

- [ ] 6.0 Live transcription — stream the transcript into the bar and paste the streamed result
  - [ ] 6.1 **Do this first, it is a stop condition.** Verify `RingBuffer` does not wrap before
        `onCapReached` fires. `AudioStreamTranscriber` tracks its position with `lastBufferSize`, so
        shifting indices would make it silently re-transcribe or skip audio. If it wraps, stop and
        report — do not patch around it.
  - [ ] 6.2 Create `mac/Voice/ASR/VoiceAudioProcessor.swift` implementing `AudioProcessing` over the
        existing `AudioRecorder`. Only four members are ever called; delegate the static
        file-loading half to WhisperKit's `AudioProcessor` and make `startStreamingRecordingLive`
        throw rather than pretend. **Do not modify `AudioRecorder`.**
  - [ ] 6.3 Create `mac/Voice/ASR/StreamingTranscriber.swift` owning the `AudioStreamTranscriber`
        actor, built from the same WhisperKit instance as today (one model, not two), with
        `DecodingOptions.language` from `Preferences.language` and vocabulary from
        `DictionaryCache.shared.terms`.
  - [ ] 6.4 Start and stop the stream alongside the recorder in `Coordinator`, and publish confirmed
        and unconfirmed text for the bar.
  - [ ] 6.5 Render confirmed text plainly and unconfirmed dimmed in `HUDView`, both behind
        `Preferences.showTextInHUD`. Add no transcript text to any log line.
  - [ ] 6.6 Make the dictation's `raw` the confirmed segments joined in order and trimmed once,
        excluding unconfirmed, with tests (AC-8).
  - [ ] 6.7 Add the one-pass fallback for an empty or whitespace-only streamed result, with a test
        asserting the dictation still completes rather than being discarded.
  - [ ] 6.8 Add the single-segment rule to `SkipGate`: a streamed transcript of two or more confirmed
        segments may not skip cleanup, however clean it looks (AC-9).
  - [ ] 6.9 Audit every `padOrTrimAudio` call for `saveSegment` and confirm it is `false` — the flag
        writes audio to disk and would break the app's one stated privacy property invisibly.
  - [ ] 6.10 Measure streamed vs one-pass word error rate over the 20 pt/en fixtures in
        `server/src/cli/bench-fixtures.ts`. Record **both numbers** in `docs/SPIKES.md`. Ship step 6
        only if streamed WER is within 2 points; otherwise leave it behind a disabled flag so tasks
        0–5 still ship, and say so. Do not tune parameters to make the number pass.

- [ ] 7.0 Docs and handoff
  - [ ] 7.1 Add the macOS double-tap conflict check to `GO_LIVE.md` §2: on the `.fn` hotkey, confirm
        double-tap does not open macOS Dictation. This Mac is on `rightOption` with macOS Dictation
        disabled, so it will not reproduce here.
  - [ ] 7.2 Append the manual checks from the build spec §15 to `GO_LIVE.md` §3, including the two
        streaming ones (live text appears while speaking; nothing appears with `showTextInHUD` off).
        It is gitignored — edit it, never `git add -f` it.
  - [ ] 7.3 Final pass: full suite green, Release build via `./scripts/package.sh` succeeds and prints
        `TeamIdentifier=FZC6P6XRGD`, and report the test count against the 89 baseline.

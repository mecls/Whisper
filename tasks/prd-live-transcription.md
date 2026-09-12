# Live transcription — Implementation Spec

## 1. Objective

Today a dictation is silent until it is over: you speak, release, and then wait while the whole
utterance is transcribed in one pass. For a 30-second hands-free session that is 30 seconds of
staring at a waveform with no idea whether the words are right.

This streams the transcript into the bar as you speak, using WhisperKit's `AudioStreamTranscriber`
on the Mac's Neural Engine. The text appears in Voice's own bar, not in the app you are dictating
into.

The second effect is the one that matters more, and it inverts a constraint the project has been
fighting for months. **The pasted text comes from the streamed result**, which means that by the time
you release the key the transcript already exists. Today, release→paste contains a full ASR pass;
after this, it contains almost none of one. `tasks/prd-sub-second-dictation.md` set out to get under
a second by shaving the cleanup round trip; this removes the larger half of the budget instead.

It supersedes the "no live transcription" exclusion in `tasks/prd-hands-free-dictation.md` §6, which
was written on the assumption that this meant building streaming ASR. It does not — WhisperKit 0.18.0
already ships it.

## 2. Business rules (invariants — never violate)

### Where transcription happens

1. **ASR stays on the Mac. Never send audio to the server for transcription.** The VPS is 2 vCPU /
   7.8 GB with no GPU and runs no ASR at all today — it exists to call an LLM for cleanup. Whisper
   `large-v3-turbo` there would run several times slower than realtime before adding a network
   round trip per chunk, against a Neural Engine that already beats realtime locally. This rule is
   not a preference; it is the reason streaming is feasible at all.

2. **One model instance, not two.** Streaming and any fallback pass share the single WhisperKit
   instance built from `Preferences.modelId`. The compressed model is ~630 MB and takes 4–5 minutes
   to compile on first run; loading a second copy would double memory and stall the app.

### What reaches the user's document

3. **Live text is display-only. It is never typed into the target application.** Whisper revises as
   it goes — `AudioStreamTranscriber.State` deliberately separates `unconfirmedSegments` from
   `confirmedSegments`, and unconfirmed wording changes. Text already pasted into someone's document
   cannot be un-typed. Everything streamed goes to Voice's own bar; the target app receives exactly
   one insertion, at the end, exactly as it does today.

4. **The pasted text is the streamed result, in every mode, with no second ASR pass — decided.**
   When the session ends, the concatenated confirmed segments become the dictation's `raw`, which
   then follows the existing path: `SkipGate` decides whether cleanup is needed, `/v1/refine` cleans
   it if so, `TextInjector` pastes it. Nothing downstream of `raw` changes, which keeps the server
   contract, the outbox, the word count and the latency metrics comparable across this change.

   Two alternatives were considered and rejected. **Re-transcribing short dictations as cheap
   insurance** is backwards: chunk damage scales with the number of boundaries, so it is worst on
   long sessions — exactly where a second pass is most expensive. **Streaming for latched sessions
   only** would leave hold-to-talk at today's latency and give up the larger half of the win.
   Cleanup is the safety net for boundary artifacts, since fixing fillers, repeats and false starts
   is the same class of repair; rule 4a covers the one case cleanup cannot see.

4a. **A streamed transcript may only skip cleanup when it was a single confirmed segment.**
   `SkipGate` exists to save a ~700 ms round trip on short, well-formed utterances, and it is the
   one path where nothing inspects the text before it reaches the user's document — so it is the
   only place a chunk-boundary artifact could land uncorrected. A transcript assembled from one
   segment has no boundary and is therefore safe to skip; anything with two or more goes to cleanup
   regardless of how clean it looks. This keeps the fast path for exactly the short utterances where
   700 ms is the largest fraction of total latency, rather than making the gate blanket-conservative
   and paying the round trip on everything.

5. **If streaming produces nothing usable, fall back to a one-pass transcription of the buffered
   audio.** "Nothing usable" = no confirmed segments, or a concatenated result that is empty after
   trimming. The audio is still in the `RingBuffer`; transcribe it the way the app does today rather
   than losing the dictation. A dictation must never be silently dropped because the streaming path
   misbehaved — that failure mode already cost this project an afternoon once, when a 24 kHz AirPods
   stream converted to silence and produced a zero-word dictation nobody could explain.

6. **Streaming runs for every dictation, not just latched ones.** Rule 4 makes the streamed result
   the source of the pasted text, so it must always be running or a short hold-to-talk would have no
   transcript at all. The win is the same in both modes: the text is ready at release.

### Who owns the microphone

7. **`AudioRecorder` keeps ownership of audio capture. WhisperKit must not open the microphone.**
   `AudioStreamTranscriber.startStreamTranscription()` calls `audioProcessor.startRecordingLive`, so
   left alone it would take over the input device. Our capture path is where the
   `AVAudioEngineConfigurationChange` rebuild lives (AirPods switch the input from 48 kHz to 24 kHz
   and the converter must follow), where the 90-second `RingBuffer` cap lives, and where the bar's
   waveform gets its levels. Handing capture to WhisperKit would re-open a bug that was closed on
   2026-09-12 and verified across seven live device switches.

8. **Bridge with a `VoiceAudioProcessor: AudioProcessing` adapter over the existing recorder.**
   `AudioProcessing` declares a dozen members, but **`AudioStreamTranscriber` only ever calls four**
   — verified by reading its 224 lines: `startRecordingLive(callback:)`, `stopRecording()`,
   `relativeEnergy` and `audioSamples`. It never calls `purgeAudioSamples`,
   `startStreamingRecordingLive`, `resumeRecordingLive`, `pauseRecording` or the instance
   `padOrTrim`. The static half — `loadAudio(fromPath:...)`, `loadAudio(at:)`, `padOrTrimAudio(...)` —
   has nothing to do with live capture and delegates straight to WhisperKit's own `AudioProcessor`.

   So the real surface is four members, and **`AudioRecorder` itself is not modified at all**:

   | `AudioProcessing` member | Called by streaming? | Backed by |
   |---|---|---|
   | `startRecordingLive(inputDeviceID:callback:)` | **yes** | `AudioRecorder.start()`, ignoring `inputDeviceID` |
   | `stopRecording()` | **yes** | `AudioRecorder.stop()` |
   | `audioSamples: ContiguousArray<Float>` | **yes** | the `RingBuffer`'s current contents |
   | `relativeEnergy: [Float]` | **yes** | the levels `EnergyGate.rms` already computes in `consume` |
   | `relativeEnergyWindow: Int` | no | stored property; WhisperKit's default |
   | `purgeAudioSamples(keepingLast:)` | no | no-op — see rule 10 |
   | `pauseRecording()` / `resumeRecordingLive(...)` | no | map to the existing pause/start anyway, for safety |
   | `startStreamingRecordingLive(...)` | no | unreachable; throw rather than pretend |

   `inputDeviceID` is deliberately ignored: device selection is the OS's, and rule 7's rebuild
   already handles it changing.

9. **One source of audio energy, not two.** `relativeEnergy` must be derived from the same
   `EnergyGate.rms` values that drive the bar's waveform and the speech gate. Two independent energy
   measurements would drift, and the bar would then show a waveform that disagrees with the VAD
   deciding whether you are speaking.

10. **Do not add a purge operation to `RingBuffer`.** `purgeAudioSamples(keepingLast:)` exists
    because WhisperKit's own processor accumulates samples without bound; ours is a fixed-capacity
    buffer that already caps at `sampleRate * maxSeconds` and fires `onCapReached`. Streaming never
    calls it (rule 8), so the adapter implements it as a no-op and audio code stays untouched.

    **Verify one thing before relying on this:** that `RingBuffer` does not *wrap* before
    `onCapReached` ends the session. `AudioStreamTranscriber` tracks progress through the buffer with
    `lastBufferSize` and `lastConfirmedSegmentEndSeconds`; if indices shifted underneath it, it would
    silently re-transcribe or skip audio. If it does wrap, say so and stop — that is a design fork,
    not something to patch around.

### Privacy

11. **Live text obeys `Preferences.showTextInHUD`.** With it off the bar shows the waveform and the
    stage, and no words at all. One switch governs every appearance of transcript text on screen, so
    the property stays simple enough to state in one sentence and check in one place. Someone
    dictating a password or sitting in a meeting turns it off once and is done.

12. **No transcript text and no audio may reach disk — including WhisperKit's debug paths.**
    `AudioProcessing.padOrTrimAudio` takes a `saveSegment: Bool` that writes the audio segment out
    for debugging; it must be `false` everywhere, and the adapter must never pass `true`. The app's
    stated privacy property is that transcripts never land on the Mac's disk, and a debugging flag
    left on in a library call would break it without appearing anywhere in this codebase.

13. **No transcript text in logs.** Follow the existing convention: log lengths and counts
    (`rawChars`), never content. This applies to streaming's per-chunk callbacks, which fire many
    times per dictation and would otherwise fill the log with the user's speech.

### Correctness of the text

14. **Streaming gets the same language and vocabulary hints the one-pass path gets.**
    `DecodingOptions.language` comes from `Preferences.language` (nil when `auto`), and the
    dictionary terms from `DictionaryCache.shared.terms` — the same values `TranscribeHint` carries
    today. A streamed dictation that spells "Miraside" wrong where a held one gets it right would be
    a regression the user experiences as the feature making things worse.

15. **The final text is the confirmed segments joined in order, trimmed once.** Do not include
    `unconfirmedSegments` in what gets pasted: they are the hypotheses Whisper has not settled on,
    and including them is how you paste a half-revised word.

## 3. Flows

**A dictation, start to finish**
1. Hotkey down (or bar mic click) → `AudioRecorder.start()` as today → `AudioStreamTranscriber`
   begins consuming from the adapter.
2. As audio arrives, WhisperKit's VAD segments it and the state callback fires with updated
   `confirmedSegments` / `unconfirmedSegments`. The bar renders confirmed text plainly and
   unconfirmed text dimmed, subject to rule 11.
3. Hotkey up (hold) or second tap (latched) → capture stops. The confirmed segments are already
   transcribed; join and trim them (rule 15) into `raw`.
4. `SkipGate` runs on that `raw` exactly as today, then cleanup or not, then insertion. Unchanged.
5. If step 3 yields nothing usable → one-pass transcription of the ring buffer (rule 5), then
   continue at step 4.

**Where a step can fail**
- **Stream never starts** (model not ready, adapter throws): fall back to today's behaviour for the
  whole dictation — capture, then one-pass at the end. The user sees no live text and a normal
  paste; log it once, do not retry mid-dictation.
- **A chunk fails to decode:** WhisperKit's own fallback handling applies; do not add a second retry
  layer on top of it.
- **Input device changes mid-dictation:** `AudioRecorder` rebuilds its capture chain (already
  implemented and tested). The adapter must survive the format change without restarting the stream,
  because the samples reaching the ring buffer are already converted to the 16 kHz target format.
- **90-second cap:** `onCapReached` ends the session; the confirmed text so far is what gets pasted.

## 4. Surfaces

| Surface | Change |
|---|---|
| **`Voice/ASR/VoiceAudioProcessor.swift`** (new) | The `AudioProcessing` adapter of rule 8. |
| **`Voice/ASR/StreamingTranscriber.swift`** (new) | Owns the `AudioStreamTranscriber` actor, exposes confirmed/unconfirmed text as `@Published`, handles rule 5's fallback. |
| **`Voice/ASR/Transcriber.swift`** | The existing protocol; the streaming path must satisfy the same `Transcript` contract so `DictationMachine` is unchanged. |
| **`Voice/Audio/RingBuffer.swift`** | New `purge(keepingLast:)` (rule 10). |
| **`Voice/Audio/AudioRecorder.swift`** | Expose the energy history and a sample stream for the adapter. No change to capture, the config-change rebuild, or the cap. |
| **`Voice/App/Coordinator.swift`** | Start/stop the stream alongside the recorder; feed live text to the bar; apply rule 5. |
| **`Voice/UI/HUDView.swift`** | Render confirmed text plainly, unconfirmed dimmed, both behind `showTextInHUD`. |
| **`docs/SPIKES.md`** | Record the accuracy comparison from §5 — streamed vs one-pass on the bench fixtures. |

No server change. No API change. No new dependency — `AudioStreamTranscriber` and `EnergyVAD` are
already in the pinned WhisperKit 0.18.0.

## 5. Validation

**The accuracy question has to be answered with numbers before this ships**, because rule 4 makes
streamed text the text the user actually gets. Chunked transcription loses context across boundaries;
the question is how much.

Run the 20 pt/en fixtures in `server/src/cli/bench-fixtures.ts` through both paths and compare:

```bash
cd mac && VOICE_ASR_TESTS=1 xcodebuild test -project Voice.xcodeproj -scheme Voice \
  -destination 'platform=macOS,arch=arm64' -only-testing:VoiceTests/StreamingAccuracyTests
```

- Word error rate, streamed vs one-pass, per fixture and in aggregate.
- **Ship if streamed WER is within 2 percentage points of one-pass.** Beyond that, rule 4 is the
  wrong default and the fallback in §7 Q1 becomes the design.
- Record both numbers in `docs/SPIKES.md`. "It seemed fine" is not a result.

Unit tests, which must not need a microphone:
- `RingBuffer.purge(keepingLast:)` — boundary cases at 0, 1, capacity, and more than held.
- Confirmed-segment joining (rule 15) — ordering, trimming, and that unconfirmed text is excluded.
- Rule 5's fallback trigger — empty segments and whitespace-only results both fall back.
- Rule 11 — with `showTextInHUD` false, the view model exposes no text in any state.

```bash
cd mac && xcodegen generate && xcodebuild test -project Voice.xcodeproj -scheme Voice \
  -destination 'platform=macOS,arch=arm64'
# baseline 89 tests, 1 skipped, 0 failures → 89 + new, 0 failures
```

Latency, the reason this exists — measure it rather than assume it:
- `total_ms` (release→paste, already recorded per dictation) should fall substantially, because the
  ASR pass leaves the critical path. Compare the median over 20 dictations before and after via
  `GET /v1/dictations?limit=20`.
- **Target: median `total_ms` under 1000 ms**, which is what `prd-sub-second-dictation.md` set out to
  reach and did not.

## 6. Out of scope

- **Server-side ASR.** Rule 1. The VPS has no GPU and 2 vCPU; this is settled, not deferred.
- **Typing text progressively into the target app.** Rule 3 — unconfirmed text is revised, and a
  paste cannot be taken back.
- **Editing the live text in the bar.** The bar displays; it is not a text field.
- **Raising the 90-second cap.** Unchanged from `prd-hands-free-dictation.md`.
- **Changing the cleanup path, the skip gate, the outbox or the server contract.** Rule 4 exists to
  keep all of it untouched, so the latency and word-count metrics stay comparable across the change.
- **A settings UI for chunk size, VAD threshold or confirmation count.** Use WhisperKit's defaults
  (`requiredSegmentsForConfirmation: 2`, `silenceThreshold: 0.3`, `useVAD: true`) until there is a
  measured reason not to.

## 7. Open questions

1. **What happens if streamed WER is worse than 2 points?** Rule 4 is decided either way — a second
   ASR pass is not the answer, because the case that needs it most is the case it costs most in. So
   the remedy is to tune the stream rather than hedge it: `requiredSegmentsForConfirmation` (default
   2) trades latency for stability, and raising it means confirming on more agreement. That is a
   parameter change, not a design change. **Still open:** what the number should be, and whether
   there is a WER bad enough to reconsider rule 4 entirely. Measure first.
2. **Does always-on streaming cost meaningful battery or thermal headroom?** It runs the Neural
   Engine continuously during every dictation rather than once at the end. Unknown; worth measuring
   on battery before rule 6 is considered settled.
3. ~~**Does the skip gate still behave the same on streamed text?**~~ **Answered by rule 4a:** a
   streamed transcript may only skip when it was a single confirmed segment, so no text carrying a
   chunk boundary can reach the user uninspected. Still worth re-running the gate's own fixtures
   against streamed output to confirm punctuation at segment ends has not shifted — the gate keys on
   terminal punctuation, and a transcript ending mid-sentence would be sent for cleanup where a held
   one skipped. That is a test to write, not a decision to make.
4. **Should the bar show interim text for hold-to-talk dictations too?** Rule 6 runs streaming for
   all of them, but a 3-second hold may not be on screen long enough for live text to be anything but
   flicker. Possibly display live text only in latched sessions while still using the streamed result
   everywhere.

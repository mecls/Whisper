# Sub-Second Dictation — Implementation Spec

## 1. Objective

A dictation currently pastes 2–5 s after the key is released, and the measured breakdown says it
will get **worse** once the server is reachable, not better. The pipeline is strictly serial in
`Coordinator.perform` — `.transcribe` → `.refine` → `.insert`
(`mac/Voice/App/Coordinator.swift:115-143`) — and every stage sits on the critical path after
release: ~0.9 s ASR plus a 0.7–0.9 s LLM round trip (gemma4 p50, `docs/SPIKES.md`) across two
network hops (Mac → VPS → ollama.com). Today those cleanup calls are all failing on an untrusted
TLS certificate, so what is being felt right now is the ASR-only path.

This spec moves cleanup **off the network and onto the device** using Apple's `FoundationModels`
framework (macOS 26, no download, no API key, already available on the target Mac), lets the app
**skip cleanup entirely** for utterances that do not need it, and **pre-warms** the connection that
remains. The server keeps history, dictionary, auth, and stays as the cleanup fallback.

Target: **p50 ≤ 800 ms and p90 ≤ 1200 ms** from key release to paste, on a 10–12 s dictation, on an
idle Mac, over at least 30 real dictations.

There is a second reason to do this now. `POST /v1/dictations` hardcodes `cleaned: null`,
`llmMs: null`, `llmModel: null` (`server/src/routes/dictations.ts:15,18`) because until now the only
way text got cleaned was `POST /v1/refine`, which writes the row itself. The moment cleanup happens
on-device, every dictation would be stored raw with no timings — history would silently degrade and
the acceptance number above could not be measured at all.

For the open-source story this also removes the VPS from the critical path: the app becomes useful
with no server configured.

---

## 1a. Status — open question 1 is answered, and the answer is no (2026-09-11)

§7 open question 1 made measuring `FoundationModels` the first task and set the bar: beat gemma4's
706 ms p50 by a clear margin, re-check the approach if p90 exceeds ~400 ms. Measured over the 20
`BENCH_FIXTURES` on this M2 (full detail in `docs/SPIKES.md`): **p50 583–718 ms, p90 879–906 ms** —
no clear margin — and, more seriously, the output is not safe to paste. It translates across
languages despite an explicit instruction not to, inverts meaning, and on short inputs it returns
few-shot example text verbatim, which would put words the user never said into their document.

**Consequently rules 1–8 are on hold and nothing in them may be built yet.** The cleanup engine
reverts to the server (`gemma4`, quality already validated), and the choice of a local engine — MLX
with a small instruct model, or no local engine at all — is a decision for Miguel, recorded in §7.

Everything else in this spec is independent of which engine cleans the text and proceeds as written:
the skip gate (rules 11–14), the `totalMs` instrumentation (rules 15–18), the connection pre-warm
(rules 19–21) and the non-regression set (rule 22). Two of those get *more* important, not less: with
the server as the only cleanup engine, pre-warm now sits on the main path rather than a fallback, and
the gate is the only remaining change that removes a network round trip from a dictation entirely.

§7 open question 2 (pt-PT quality) is also answered by the same run and needs no separate fixture.

---

## 2. Business rules (invariants — never violate)

### Cleanup engine selection — ON HOLD, see §1a

1. **Cleanup is attempted in exactly this order: gate → local → server → raw.** The first one that
   produces text wins; each step down is a fallback, never a parallel race. No step may be skipped
   because an earlier one was slow — a slow local attempt must be *abandoned* (rule 4), not left
   running alongside a server call, because two engines writing the same dictation is how the queue
   in `DictationMachine` ends up inserting text twice.

2. **`LocalRefiner` never talks to the network, and `RefineService` never runs a model.** The two are
   composed by a new `HybridRefiner: Refiner` which owns the order in rule 1 and delegates *all*
   server logging (`logOnly`, `replayOutbox`, `reportInjected`, outbox replay) to the existing
   `RefineService`. `Coordinator.perform`'s `.refine(id)` effect and the `Refiner` protocol
   (`mac/Voice/Refine/Refiner.swift`) must not change shape — this feature swaps what is injected,
   not the seam.

3. **`FoundationModels` availability is resolved once per launch and cached.** The availability check
   (`SystemLanguageModel.default.availability`) must never run inside a dictation. If it reports
   anything other than `.available`, `LocalRefiner` reports unavailable for the whole session and
   every dictation goes straight to the server path. Reason: Apple Intelligence can be off, the
   device can be unsupported, and the model can be mid-download — none of those may cost a user
   200 ms per dictation to rediscover.

4. **The local cleanup budget is a hard deadline of 600 ms.** On expiry the local task is cancelled
   and the dictation falls through to the server path with `fallbackReason = local-timeout`. 600 ms
   is chosen so that local-then-server worst case (600 + server budget) still terminates, and so a
   local stall can never exceed the ASR time it was meant to hide. **Calibrate against real
   `FoundationModels` latency before shipping — see §7.**

5. **A guardrail refusal is a fallback, not an error.** If `FoundationModels` refuses the input or
   returns a safety error, that is `fallbackReason = local-refused` and the dictation continues down
   the chain. It must never surface as an error dialog and must never lose the transcript. Reason:
   the model applies its own content policy to arbitrary dictated speech, and a user dictating a
   medical or legal note must still get their words pasted.

6. **Empty local output counts as a refusal.** Mirrors the existing server rule
   (`RefineService.swift:34`, `r.cleaned.isEmpty` → `.guardRejected`): empty text is never pasted
   over a non-empty transcript.

7. **Literal mode never reaches any cleanup engine.** `mode == "literal"` returns `.literal` before
   the gate, before local, before the network — preserving `RefineService.swift:24-27`. The user
   asked for the raw transcript; anything else is a bug.

8. **The cleanup prompt has exactly one source of truth.** `docs/PROMPT.md` remains authoritative;
   `LocalRefiner` uses the same rule text as `buildSystemPrompt` (`server/src/llm/prompt.ts`). If
   the two ever diverge, the same sentence gets cleaned two different ways depending on which engine
   ran, which is indistinguishable from a bug to the user. A shared fixture test asserts both
   engines produce identical output for the 5 dictation fixtures in `mac/Fixtures/`.

9. **Dictionary terms are enforced on every path, including the skipped one.** Exact-match
   replacements from `DictionaryCache.shared.terms` are applied as plain string replacement after
   cleanup (or after the gate skips it). Reason: the dictionary is the one thing the user explicitly
   configured, and rule 11 will now let text reach the caret without any model having seen it.

10. **A new fallback reason must be added to both enums in the same commit.** `FallbackReason`
    (`mac/Voice/App/Dictation.swift:6`) and `FALLBACK_REASONS` (`server/src/db/schema.ts:26-29`).
    The server validates this field with zod; a value the server does not know is a 400 on
    `POST /v1/dictations`, which silently drops the dictation into the outbox forever. New values:
    `local-unavailable`, `local-refused`, `local-timeout`, `local-error`.

### The skip gate

11. **Cleanup is skipped only when every one of these holds.** Any single failure sends the
    dictation to the cleanup engines:
    - `mode == "clean"` (literal is already handled by rule 7)
    - word count ≤ 12
    - no filler token present (rule 12)
    - no immediately repeated word (`\b(\w+)\s+\1\b`, case-insensitive) — a false start
    - no spoken-command phrase: `new line`, `nova linha`, `new paragraph`, `novo parágrafo`
    - the transcript ends with terminal punctuation (`.`, `?`, `!`, `…`)

    The last condition is the cheap proxy for "Whisper already produced a well-formed sentence".
    WhisperKit output is punctuated and capitalized already, so on a short clean utterance the
    cleanup engine has nothing left to do but cost 700 ms.

12. **The filler list is per detected language, not global.** English: `um, uh, er, ah, like` (as
    filler), `you know`, `I mean`. Portuguese: `hã, é, ééé, tipo, pronto, epá`. The list keys off
    `Dictation.language` from the ASR result; when language is `nil` or unrecognised, **both** lists
    apply, because a false negative here costs 700 ms and a false positive pastes a filler word.

13. **The gate never runs in literal mode and never modifies text.** It decides only *whether* to
    clean. Dictionary replacement (rule 9) is the sole transformation applied on the skipped path.

14. **A skipped dictation is still logged to the server** with `injected = raw`,
    `cleaned = null`, `llmMs = null`, `llmModel = "skipped"`. Reason: the skip rate is the number
    that tells you whether rule 11's thresholds are right, and it is invisible unless it is recorded.

### Measurement

15. **`totalMs` is measured from key release to paste completion, client-side, as one number.**
    Start: the instant `Effect.stopRecording` runs in `Coordinator.perform`
    (`Coordinator.swift:107`). End: the completion of `injector.insert`'s callback
    (`Coordinator.swift:139`). It deliberately includes ASR, cleanup, injection and every hop of
    dispatch overhead between them, because that is the number the user actually experiences — the
    sum of `asrMs + llmMs` has repeatedly been smaller than the felt latency, and the gap is the
    thing worth finding.

16. **`POST /v1/dictations` must accept and persist `cleaned`, `llmMs`, `llmModel` and `totalMs`.**
    Today the handler writes literal `null` for the first three
    (`server/src/routes/dictations.ts:15,18`) and `total_ms` does not exist. Required changes:
    `DictationBody` (`server/src/routes/schemas.ts`) gains the four optional fields;
    `dictations` (`server/src/db/schema.ts`) gains `total_ms integer`; `DictationRequest`
    (`mac/Voice/Refine/VoiceAPI.swift:12-15`) gains them on the client. Without this the local path
    stores raw-only history and §5 cannot be evaluated.

17. **`llmModel` records which engine actually produced the text**, one of: the server's model name
    (unchanged behaviour), `apple-fm` for local, `skipped` for the gated path. Reason: an aggregate
    latency number is meaningless if you cannot separate the three populations.

18. **`totalMs` is recorded even when the dictation never reaches the server.** It rides the outbox
    entry (`OutboxEntry`, `mac/Voice/Refine/Outbox.swift`) like `audioMs` and `asrMs` already do, so
    offline dictations still contribute to the percentile once connectivity returns.

### Connection pre-warm

19. **Pre-warm fires on `hotkeyDown`, never on app launch, and never blocks recording.** A
    best-effort `HEAD /health` on the shared `URLSession` at `MachineEvent.hotkeyDown`
    (`DictationMachine.swift:42`), dispatched without `await`. `try? recorder.start()` must run
    first — no network call may ever be between the user pressing the key and the microphone
    opening.

20. **Pre-warm is skipped when no server call is expected.** If `LocalRefiner` is available *and*
    the last 10 dictations all used the local or skipped path, do not warm. Reason: with local
    cleanup as the default, the only guaranteed server call is the history POST *after* the paste,
    which is off the critical path; warming on every key press would send steady background traffic
    to the VPS for no latency benefit.

21. **A failed pre-warm is silent.** No HUD, no log above `debug`, no effect on the dictation. It is
    an optimisation, and a user on a plane must not see an error for it.

### Non-regression

22. **These existing behaviours must survive unchanged**, each already covered by a manual checklist
    item: clipboard restore after paste, secure-input detection routing to clipboard, Esc cancel
    during transcribe, back-to-back dictations pasting in order, and offline outbox replay. The
    cleanup engine changed; nothing about injection did.

---

## 3. Flows

**A. Normal dictation, local cleanup (the target path)**
1. `hotkeyDown` → recorder starts → pre-warm dispatched if rule 20 allows.
2. `hotkeyUp` → `stopRecording`, `totalMs` clock starts, energy gate runs.
3. `.transcribe` → WhisperKit → `raw`, `language`, `asrMs`.
4. `HybridRefiner`: gate (rule 11) says clean is needed → `LocalRefiner` with a 600 ms deadline.
5. Local returns cleaned text → dictionary replacement → `.insert` → paste → `totalMs` stops.
6. `reportInjected` → `POST /v1/dictations` with `cleaned`, `llmMs`, `llmModel = "apple-fm"`,
   `totalMs`. On failure the entry goes to the outbox and replays later.

**B. Gate skips cleanup**
Steps 1–3 as above. Gate returns skip → dictionary replacement only → paste. Logged with
`llmModel = "skipped"`, `injected = raw`. HUD shows the skipped state (rule 24).

**C. Local unavailable or refuses**
At step 4, `LocalRefiner` reports unavailable (rule 3), refuses (rule 5), returns empty (rule 6) or
misses its deadline (rule 4). The dictation continues to `RefineService` exactly as it behaves
today, carrying the matching `local-*` fallback reason. If the server also fails, the existing raw
fallback applies and the entry is queued in the outbox — unchanged from current behaviour.

**D. Literal mode**
`hotkeyUp` → transcribe → `.literal` returned before the gate → raw pasted → `logOnly` with
`fallbackReason: null` (preserving the `G2` comment at `RefineService.swift:67-68`).

---

## 4. Surfaces

| Surface | Change |
|---|---|
| HUD | New state for the skipped path (rule 24). `Strings.cleaning` continues to show only while a cleanup engine is actually running. |
| `Strings.swift` | New: skipped-path label; `pastedRaw` copy revisited — "server slow or offline" is wrong when the local engine refused. |
| Settings › Model | Read-only row: which cleanup engine is active (`On-device (Apple Intelligence)` / `Server` / `Unavailable`), so a user can tell why their latency changed. |
| `POST /v1/dictations` | Accepts `cleaned`, `llmMs`, `llmModel`, `totalMs` (rule 16). Backwards compatible: all optional, older clients keep working. |
| `dictations` table | New column `total_ms integer`. |

23. **The Settings row is read-only.** No engine picker. Rule 1's order is the product decision; a
    toggle would double the support surface and the number of paths under test.

24. **The HUD must distinguish the three cleanup outcomes during rollout**: cleaned locally, cleaned
    by server, skipped. Answer 3B chose visibility deliberately — the gate's thresholds (rule 11)
    are guesses until they are watched firing on real speech. This is a rollout affordance and is
    expected to be reconsidered once the thresholds settle; it is not permanent UI.

---

## 5. Validation

**The acceptance number.** After ≥30 real dictations, on the VPS:

```sql
WITH s AS (
  SELECT total_ms FROM dictations
  WHERE created_at > (strftime('%s','now') - 7*86400) * 1000
    AND audio_ms BETWEEN 10000 AND 12000
    AND total_ms IS NOT NULL
  ORDER BY total_ms
)
SELECT (SELECT COUNT(*) FROM s)                                                    AS n,
       (SELECT total_ms FROM s LIMIT 1 OFFSET (SELECT COUNT(*)*50/100 FROM s))     AS p50_ms,
       (SELECT total_ms FROM s LIMIT 1 OFFSET (SELECT COUNT(*)*90/100 FROM s))     AS p90_ms;
```

**Passes when `n ≥ 30`, `p50_ms ≤ 800`, `p90_ms ≤ 1200`.**

**Engine split** — confirms the gate and local path are actually carrying the traffic, not silently
falling back:

```sql
SELECT llm_model, COUNT(*) AS n, AVG(total_ms) AS avg_ms
FROM dictations WHERE created_at > (strftime('%s','now') - 7*86400) * 1000
GROUP BY llm_model ORDER BY n DESC;
```

Expect `apple-fm` + `skipped` ≥ 90% of rows. A large `gemma4` count means local is failing and the
p50 above is being met by luck.

**Unit tests (no microphone, no network, must run in CI):**
- Gate: a fixture table of ≥20 transcripts, en and pt, each with its expected skip/clean decision,
  covering every clause of rule 11 individually.
- Prompt parity (rule 8): local and server engines produce identical output on the 5 fixtures.
- Fallback chain: `LocalRefiner` stubbed to unavailable / refuse / empty / timeout, asserting the
  right `local-*` reason reaches `DictationRequest` in each case.
- Enum parity (rule 10): a test that fails if `FallbackReason` and `FALLBACK_REASONS` diverge.

**Manual (needs a human, extends the `GO_LIVE.md` §3 checklist):**
- Dictate a short clean sentence → HUD shows skipped, paste is near-instant.
- Dictate with fillers → HUD shows local cleaning, fillers are gone.
- Turn Apple Intelligence **off** in System Settings → dictate → still works, via the server, and
  the Settings row reads `Server`.
- Turn Wi-Fi off with Apple Intelligence on → dictate → still cleaned locally, and the row appears
  in `/v1/dictations` after reconnecting.

---

## 6. Out of scope

- **Streaming / incremental ASR.** Deferred (answer 5A). Whisper always encodes a padded 30-second
  window, so chunking during speech still pays full encoder cost per chunk — it trades battery for
  roughly 0.3–0.4 s and should be re-measured only after the changes here land.
- **A different or additional ASR model.** No new download, no "Fast/Accurate" picker (answer 5D).
  The ~0.45 s encoder floor stays as-is this round.
- **Optimistic paste-then-correct.** No inserting raw text and rewriting it afterwards; that needs
  backspacing into a foreign app's text field and is the single most likely way to corrupt a user's
  document.
- **Any change to the hotkey, event tap, `TextInjector` or clipboard restore.** Rule 22 makes this
  explicit — those paths are already validated by manual checklist and are not part of this work.
- **Removing server-side cleanup.** Answer 2B keeps `POST /v1/refine` and the gemma4 path exactly as
  they are.

---

## 7. Open questions

1. ~~**What is the real `FoundationModels` latency for this prompt on an M2?**~~ **ANSWERED
   2026-09-11 — p50 583–718 ms, p90 879–906 ms, and unsafe output.** See §1a and `docs/SPIKES.md`.
   Rules 1–8 are on hold.
2. ~~**Does `FoundationModels` clean pt-PT to the same standard as English?**~~ **ANSWERED by the
   same run — no.** It translated Portuguese to English in both prompt variants despite an explicit
   instruction not to, and stripped accents (`está` → `esta`). No separate pt fixture was needed.
2a. **What replaces it, if anything?** Three live options: (a) keep server-only cleanup and let the
   skip gate carry the latency win — cheapest, ships today, and is what the code now does; (b) MLX
   with a small instruct model (Qwen 2.5 1.5B / Llama 3.2 3B), which costs a ~1 GB download and a
   new runtime dependency but is controllable in a way the OS model is not; (c) re-test
   `FoundationModels` on a future OS revision — `scratchpad/fmbench*.swift` re-runs in one command.
   **Decides: Miguel. Nothing else in this spec is blocked on it.**
3. **Is word count ≤ 12 the right gate threshold (rule 11)?** Chosen to be conservative. The honest
   answer comes from the `llm_model` split query in §5 after a week of real use. **Decides: Miguel,
   after rollout — this is why answer 3B put the outcome in the HUD.**
4. **Do any teammates lack Apple Intelligence?** If a teammate's Mac is unsupported or has it off,
   they get the old latency permanently and rule 20 will keep pre-warming for them. Worth knowing
   before this is presented as "the app is now fast". **Decides: Miguel.**
5. **Should `Strings.pastedRaw` still say "server slow or offline"?** With local cleanup it will
   often be neither. Needs new copy per fallback reason, or one honest generic sentence.
   **Decides: whoever implements §4.**

# Miraside Voice — a Wispr-style dictation app for macOS (plan)

## Context

The team uses Wispr Flow daily and wants its own version: hold a key, speak, release, and clean text lands at the cursor. Owning it means we can tune the cleanup, keep history and a shared dictionary on our own VPS, and pay nothing per user. The original brief asked for cloud ASR + cloud LLM; this session established two facts that reshape the ASR half:

- **Ollama Cloud has no speech-to-text.** Its Gemma 4 cloud variants (`gemma4:cloud`, `gemma4:31b-cloud`) are text+image only; audio input exists only for the local e2b/e4b/12b checkpoints and is reported as poor. So Ollama Cloud runs the **LLM cleanup**, and ASR must run elsewhere.
- **"Free and fast" rules out the VPS for ASR.** The box is ≤4 vCPU / 16 GB, shared with the ARwatches worker. Whisper pads every clip to a 30 s window, so a 6 s dictation on CPU costs 3–6 s. On the M2, WhisperKit runs `large-v3-turbo` in about 1.2–2 s regardless of clip length under 30 s.

**Decision: Whisper runs on the Mac (WhisperKit, CoreML/ANE), Ollama Cloud refines the text through a small backend on the VPS, and the VPS stores text only.** Audio never leaves the Mac. The ASR layer is a Swift protocol so a remote provider (VPS `faster-whisper`, Groq) can be added later without touching the rest.

Decisions confirmed with Miguel in this session:

| Question | Answer |
|---|---|
| ASR | Free + fast → on-device Whisper (WhisperKit `large-v3-turbo`); VPS faster-whisper kept as a future provider |
| LLM | Ollama Cloud, house path (`openai` SDK → `https://ollama.com/v1`), default `gpt-oss:120b` at `reasoning_effort: low`, to be confirmed by the bench |
| Users | Small Miraside team; per-person device tokens issued by a CLI; no login UI in v1 |
| Storage | VPS keeps text only (raw + cleaned transcripts, settings, dictionary, usage); audio discarded immediately, never written to disk anywhere |
| Hosting | Same VPS as ARwatches, behind its existing Traefik (audited), subdomain `voice.miraside.co` (confirmed) |
| Apple account | None (personal Xcode). Apple-Development signing in v1; notarization + auto-update is a later milestone |
| Location | `hub/miraside-voice/` (kebab-case like `miraside-dashboard`, `convex-backend`), its own git repo like every hub app |

Verified environment: macOS 26.5, Apple M2, Xcode 26.6, Node 22.11, Ollama.app and Wispr Flow installed (Wispr is the UX reference). The plan was adversarially reviewed once (24 findings) and the fixes are folded in below; the ones that changed the design are marked **(review)**.

---

## 1. Architecture

```
┌──────────────────────────── Mac (Swift/SwiftUI, menu bar app) ────────────────────────────┐
│                                                                                            │
│  Hotkey (CGEventTap, Fn hold) ─► AudioRecorder (AVAudioEngine → 16 kHz mono Float32)       │
│        │                                │                                                  │
│        │ release                        ▼                                                  │
│        └──────────────────► Transcriber protocol ──► WhisperKitTranscriber (on-device)     │
│                                         │ raw text                                         │
│                                         ▼                                                  │
│                              RefineClient (HTTPS, length-scaled budget) ────┐              │
│                                         │ cleaned (or raw on timeout)       │              │
│                                         ▼                                   │              │
│                              TextInjector (clipboard + ⌘V, restore)         │              │
│                                                                             │              │
│  HUD (non-activating NSPanel) · MenuBarExtra · Settings · Keychain token     │              │
└─────────────────────────────────────────────────────────────────────────────┼──────────────┘
                                                                              │ text only
┌──────────────────────────── VPS (docker compose) ────────────────────────────┼──────────────┐
│  Traefik (existing, TLS, voice.miraside.co) ─► voice-api (Node 22 + Fastify) ▼              │
│      auth (bearer device tokens) · /v1/refine · /v1/me · /v1/dictations · /v1/dictionary   │
│      SQLite (WAL) on a volume · LLM semaphore    Ollama Cloud (https://ollama.com/v1)      │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

**Why this split.** Everything latency-critical (hotkey, audio, ASR, paste) is local and works offline. The backend owns the only shared secret (the Ollama key), the team's history, and the dictionary. The wire carries text only, which makes "text only, audio deleted" a structural property rather than a policy.

### End-to-end flow with latency budget (5–10 s dictation)

| Step | Where | Target |
|---|---|---|
| Fn down → recording starts, HUD "Listening…" (HUD itself is delayed 150 ms so Fn+arrow combos never flash it) | Mac | first audio buffer < 50 ms with a pre-started engine (see §3.3) |
| Fn up → stop, energy VAD check (discard if silent or < 400 ms) | Mac | ~0 |
| WhisperKit transcribe (`large-v3-turbo`, no timestamps, no temperature fallbacks) | Mac (ANE/GPU) | **~0.9 s** steady-state on the M2 for a 12 s clip (measured, docs/SPIKES.md); ~1.7 s for the first call after launch |
| `POST /v1/refine` → Ollama Cloud, low reasoning | VPS → Ollama | 0.5–1.5 s for short input; budget scales with length (§3.5) |
| Paste via clipboard + ⌘V, restore after the app has read it | Mac | ~150–400 ms |
| **Total** | | **~2–3 s** measured pieces: 0.9 s ASR + 0.7 s cleanup (gemma4) + paste (Wispr Flow is ~1–2 s; streaming ASR is the v2 lever) |

If the refine call misses its budget the raw Whisper text (already punctuated) is pasted and the HUD says so.

---

## 2. Repository layout — `hub/miraside-voice/`

```
miraside-voice/
├── README.md                 # what it is, install for a teammate, the event-tap privacy note, manual checklist
├── AGENTS.md                 # house style: every rule states what went wrong (tone of hub/miraside/AGENTS.md)
├── docs/
│   ├── PLAN.md               # this plan, copied on M0
│   ├── ARCHITECTURE.md       # §1, kept current
│   ├── API.md                # the contract in §4.4
│   ├── PROMPT.md             # the refine prompt + the bench table that justified the model choice
│   └── SPIKES.md             # day-0 measurements (ASR latency, tap behaviour, Ollama tier/overhead)
├── mac/                      # Xcode project "Voice" (bundle id co.miraside.voice, LSUIElement = YES)
│   ├── Voice.xcodeproj
│   ├── Voice/
│   │   ├── App/        VoiceApp.swift, AppState.swift (state machine + FIFO), Dictation.swift (value type)
│   │   ├── Hotkey/     HotkeyMonitor.swift, HotkeyChoice.swift
│   │   ├── Audio/      AudioRecorder.swift, RingBuffer.swift, LevelMeter.swift
│   │   ├── ASR/        Transcriber.swift (protocol), WhisperKitTranscriber.swift, ModelManager.swift
│   │   ├── Refine/     VoiceAPI.swift (client), RefineService.swift (budget + fallback), Outbox.swift (in-memory)
│   │   ├── Inject/     TextInjector.swift, PasteboardSnapshot.swift, FrontmostContext.swift, SecureInput.swift
│   │   ├── UI/         MenuBar.swift, HUDPanel.swift, HUDView.swift, SettingsView.swift, OnboardingView.swift, Strings.swift
│   │   ├── Storage/    Keychain.swift, Preferences.swift, DictionaryCache.swift
│   │   └── Support/    Log.swift (OSLog, private), Permissions.swift
│   ├── VoiceTests/           # XCTest: state machine/FIFO, pasteboard snapshot, hotkey interpretation, text guards, budget
│   └── Fixtures/             # 3 short wavs (pt, en, silence) for the opt-in ASR integration test
├── server/                   # Node 22 + TypeScript + Fastify, own package.json (npm, like every hub app)
│   ├── src/
│   │   ├── index.ts          # bootstrap, plugins, listen
│   │   ├── env.ts            # lazy getters that throw (copy hub/LoanAgent/lib/env.ts) + LLM_CONCURRENCY + prod refusal of LOG_TRANSCRIPTS
│   │   ├── auth.ts           # bearer token → user; sha256 lookup; last_used throttle
│   │   ├── routes/           health.ts, me.ts, refine.ts, dictations.ts, settings.ts, dictionary.ts
│   │   ├── llm/              client.ts (openai singleton + semaphore), refine.ts (prompt + guards), errors.ts (copied classifyLlmError + llm-busy/llm-truncated)
│   │   ├── db/               schema.ts (drizzle), client.ts, migrate.ts, migrations/
│   │   └── cli/              users.ts (add | list | revoke), bench.ts (prompt/model/concurrency benchmark)
│   ├── test/                 # node --test via tsx (house pattern from miraside-dashboard)
│   ├── Dockerfile
│   └── .env.example          # box-drawing section headers, every var explains why it exists
└── deploy/
    ├── docker-compose.yml    # voice-api + caddy + backup sidecar; separate compose project from ARwatches
    ├── Caddyfile
    └── VPS.md                # port audit, DNS, provider firewall, first deploy, tokens, backup/restore, rollback
```

Reuse from the hub (copy, do not import — the hub is many repos, not a monorepo):

- `hub/LoanAgent/lib/env.ts` — the lazy-getter env pattern.
- `hub/miraside/lib/llm/errors.ts` — `classifyLlmError()`; Ollama's error body is `{"error": "<string>"}`, not OpenAI's `{error:{message}}`, and it separates 402 billing / 403 entitlement / 404 model. Drop its `llmProvider()` import from `./run-tool` (one provider here) and add `llm-busy` and `llm-truncated` kinds.
- `hub/tools/ContentAgent/lib/agent/llm.ts` — the cached `OpenAI` singleton with `maxRetries: 0`; the semaphore lives next to it.
- `hub/LoanAgent/.env.example` — the env file style.
- Not reused: `run-tool.ts`. It forces a tool call and runs a repair loop; Ollama Cloud rejects forced `tool_choice`, and for a plain-text rewrite the loop only adds latency. Refine is a chat completion that returns text.

---

## 3. macOS client design (SwiftUI, macOS 15+ deployment target)

Not sandboxed (CGEventTap and Accessibility are impossible inside the sandbox; not App Store). Hardened runtime off for local builds. **Swift 5 language mode with `-strict-concurrency=targeted`** **(review)**: the `CGEventTap` C callback and the realtime audio tap block are exactly the two places Swift 6 strict isolation turns into a day of `Sendable` errors; note this in AGENTS.md. `LSUIElement = YES` so the app never appears in the Dock or ⌘-Tab.

### 3.1 State machine — `AppState`

```
modelLoading ─► idle ─(Fn down)─► listening ─(Fn up)─► transcribing ─► refining ─► inserting ─► done ─► idle
                              └─(Esc / any keyDown / < 400 ms / silence)─► cancelled ─► idle
                                                              any failure ─► error(message) ─► idle (after 2.5 s)
```

- **Burst dictation is allowed** **(review)**: Fn may start a new `listening` while the previous dictation is in `refining` or `inserting`. Dictations go through a FIFO so pastes land in order, and the second paste waits for the first one's clipboard restore. Fn during `transcribing` of the previous one is also queued (the ASR is serialized by the model anyway). Only `modelLoading` ignores Fn.
- `Dictation` is a value type carrying: `clientId` (UUIDv4 minted at Fn-up), start time, audio duration, samples, frontmost app (bundle id + name, captured **at Fn down**), raw text, cleaned text, timings, what was injected, fallback reason. Persisted nowhere on the Mac except the last one for "Copy last dictation".

### 3.2 Hotkey — `HotkeyMonitor`

- **Mechanism:** a `CGEventTap` (`.listenOnly`, session tap). **Two masks, switched with the state** **(review)**: while `idle` the tap listens to `.flagsChanged` only; during `listening` it is re-enabled with `.flagsChanged | .keyDown` so Esc-to-cancel and the "a real key was pressed while Fn was held → cancel" rule work (Fn+F5, Fn+arrows must not become dictations). The app therefore never sees keystrokes while idle; README says so, because it is the first question a cautious teammate asks.
- **Fn detection:** `.flagsChanged` where `flags.contains(.maskSecondaryFn)` toggles. Right Option is `keyCode 61` with `.maskAlternate`.
- **Tap lifecycle:** handle `.tapDisabledByTimeout` and `.tapDisabledByUserInput` in the callback by calling `CGEvent.tapEnable` again, or the hotkey silently dies after one slow callback. The tap must be created **after** the permission grant or it never fires; Input Monitoring grants often need a relaunch, so onboarding offers "Relaunch" when the tap still fails after the grant.
- **Permissions:** listen-only taps need **Input Monitoring** (`CGRequestListenEventAccess()`); posting ⌘V needs **Accessibility** (`AXIsProcessTrustedWithOptions`). Onboarding requests both and polls until granted.
- **System setting:** macOS reacts to Fn itself (emoji picker / input source / dictation). Onboarding reads `defaults read com.apple.HIToolbox AppleFnUsageType` and, if it is not `0`, shows a one-click "Open Keyboard settings" with the instruction *Press 🌐 key to → Do Nothing*.
- **Trap to design around:** TCC grants are keyed to the code signature. Sign with a stable "Apple Development" certificate from the personal team, never ad-hoc `-`, or every rebuild re-prompts for both permissions.
- Hotkey choice lives in Preferences: `fn` (default), `rightOption`, `rightCommand`. Hold-to-talk only in v1; toggle mode is roadmap.

### 3.3 Audio — `AudioRecorder`

- `AVAudioEngine` input tap → `AVAudioConverter` to 16 kHz mono Float32 → a lock-free `RingBuffer` written on the audio thread and drained by the state machine **(review)** (never append to a shared array from the realtime thread). No file is ever written.
- **Engine start latency** **(review)**: cold `start()` is 50–150 ms on the built-in mic and up to a second on AirPods (HFP switch), which clips the first syllable. Keep the engine `prepare()`d while idle and keep it running for ~20 s after each dictation, because dictations cluster. Measure Fn-down→first-buffer in M2 and decide then whether a permanently running engine (permanent orange mic dot) is worth it.
- Cap at **90 s** in v1 **(review)**; a 5-minute clip means ~10 sequential 30 s windows on the ANE and a HUD that sits on "Transcribing…" for 15 s. HUD shows a countdown in the last 15 s.
- `LevelMeter` publishes RMS per buffer for the HUD waveform.
- Energy VAD at release: if 95th-percentile RMS < threshold → `cancelled` ("Nothing heard"). This also prevents Whisper's silence hallucinations ("Obrigado por assistir…", "Subtitles by…").
- Optional start/stop click sounds (`NSSound`), on by default like Wispr.

### 3.4 ASR — `Transcriber` protocol + `WhisperKitTranscriber`

```swift
protocol Transcriber {
  var isReady: Bool { get }
  func prepare(progress: @escaping (ModelProgress) -> Void) async throws
  func transcribe(_ samples: [Float], hint: TranscribeHint, progress: ((Double) -> Void)?) async throws -> Transcript
}
struct TranscribeHint { var language: String? /* nil = detect */; var vocabulary: [String] }
struct Transcript { var text: String; var language: String; var durationMs: Int }
```

- **Package:** WhisperKit via SwiftPM at `https://github.com/argmaxinc/argmax-oss-swift.git` (≥ 0.9, product `WhisperKit`; the old `argmaxinc/WhisperKit` URL redirects there). Minimum macOS 14.
- **Model** **(review)**: default `large-v3-v20240930_turbo_632MB` from HF repo `argmaxinc/whisperkit-coreml` — the compressed **turbo**, the right default on an 8 GB M2. Offer the full `large-v3-v20240930_turbo` as "maximum accuracy". Do **not** offer `large-v3-v20240930_626MB`: it is the full 32-layer large-v3, slower than turbo. Distil variants are English-only, excluded (the team dictates pt-PT and en).
- **Decoding options** **(review)**: `temperature 0`, **`temperatureFallbackCount: 0`** (the default of 5 turns every guard failure into five re-decodes and a 1 s transcription into 5 s), **`detectLanguage: true` whenever `language == nil`** (it defaults to false when `usePrefillPrompt` is on, and WhisperKit then prefills English, so pt-PT audio comes back garbled), `usePrefillPrompt: true`, `withoutTimestamps: true`, `wordTimestamps: false`, `skipSpecialTokens: true`, plus `compressionRatioThreshold` / `logProbThreshold` / `noSpeechThreshold` hallucination guards. `promptTokens` is built from the dictionary terms (first ~150 tokens) so names like *Miraside*, *Convex*, *Ollama* are spelled right at the ASR stage. `chunkingStrategy: .vad` with a small `concurrentWorkerCount` for clips over 30 s, with a progress callback into the HUD.
- **Model lifecycle (`ModelManager`):** download with progress on first run (~630 MB), then a one-time CoreML specialization on ANE that can take minutes; on every launch call **`prewarmModels()`** in the background (loading alone is not enough) so the first dictation is not the slow one. Menu shows a "Model: ready / loading 42 % / not downloaded" line. Cache dir: Application Support/Voice/models.
- **Compute units:** audio encoder on `.cpuAndNeuralEngine`, decoder on GPU (WhisperKit's default recommendation on Apple Silicon); expose in a hidden debug setting because the fastest combination differs per chip.
- Future providers behind the same protocol: `RemoteTranscriber` (VPS `faster-whisper` or Groq, FLAC encoded on the fly), `AppleSpeechTranscriber` (macOS 26 `SpeechAnalyzer`, no download, pt-PT locale support unverified), Parakeet v3 via FluidAudio (25 languages incl. pt, faster than Whisper).

### 3.5 Refine — `VoiceAPI` + `RefineService`

- `VoiceAPI` is a thin `URLSession` client: base URL + bearer token (from Keychain), JSON in/out, `User-Agent: miraside-voice/<version>`, 10 s timeout on everything except refine.
- **Length-scaled budget** **(review)**: `budgetMs = clamp(2500 + 12 × rawChars, 3500, 15000)`. The client sends `budgetMs` on `/v1/refine`, races the call against that timer, and the server runs the LLM with `timeout = budgetMs − 700` (capped at 14 000) so the server answers before the client gives up. HUD switches to "Cleaning…" after 3 s. Result is `.cleaned(text)`, `.rawFallback(reason)` where reason ∈ `client-timeout | offline | unauthorized | server`, or `.literal` when the mode skips the LLM.
- **`clientId` is the idempotency key** **(review)**: minted at Fn-up, sent on `/v1/refine`, `POST /v1/dictations` and `PATCH /v1/dictations/by-client/:clientId`. The server upserts on `(user_id, client_id)`, so the common race (server stores and answers at 3.9 s, client gave up at 3.5 s and later replays) yields one row, and `injected` can be reported without ever having received a server id.
- After injection, `PATCH …/by-client/:clientId {injected}` fire-and-forget. When the refine call never reached the server, `Outbox` keeps `{clientId, raw, injected, reason, createdAt}` **in memory only** **(review)** (no plaintext transcripts on disk; lost on quit, which is acceptable for v1), capped at 200 and replayed with `POST /v1/dictations` on the next successful request. `literal` mode pastes immediately and logs through the same path.
- `GET /v1/me` on launch and every 10 minutes refreshes settings + dictionary. **Server settings win on sync**; the client writes changes through `PUT /v1/settings` and mirrors into `UserDefaults` only as an offline cache **(review)**. Unauthorized → menu bar icon gets a badge and Settings › Server opens on click.

### 3.6 Text injection — `TextInjector`

- **v1 method: clipboard paste, restored after the app has actually read it** **(review)**. The text is written through an `NSPasteboardItemDataProvider`, so the pasting app's read arrives as `pasteboard(_:item:provideDataForType:)`; the snapshot is restored ~100 ms after that callback, with a 1.5 s ceiling if no read ever comes. (`changeCount` moves on writes, not reads, so it cannot detect a late reader; a fixed 250 ms delay would sometimes restore before Electron/JetBrains apps read and paste the user's *previous* clipboard.)
- `PasteboardSnapshot` copies a **whitelist** of types (string, RTF, HTML, URL, file URL, PNG/TIFF under 5 MB), skips promised/lazy types (Finder file promises, huge RTFD) and skips restore entirely when the snapshot is not restorable. Marks the transient item with `org.nspasteboard.TransientType` and `ConcealedType` so clipboard managers ignore it, and calls `prepareForNewContents(with: .currentHostOnly)` so the transcript is never synced to the user's iPhone by Universal Clipboard.
- ⌘V is posted as `CGEvent` key down/up (`keyCode 9`, `.maskCommand`) to `.cghidEventTap`.
- **Secure input:** `IsSecureEventInputEnabled()` true (password fields, Terminal with Secure Keyboard Entry) → do not paste, HUD "Secure field — text copied to clipboard instead", leave the text on the clipboard. Whether Fn `flagsChanged` is even delivered to the tap while secure input is on is undocumented: the day-0 spike answers it, and if it is not, the app polls `IsSecureEventInputEnabled()` while idle and badges the icon with the reason.
- **Frontmost context:** `NSWorkspace.shared.frontmostApplication` captured at Fn down; sent to the server for history and future per-app modes.
- Cut from v1 **(review)**: AX smart spacing (returns nothing in Electron/web views and is a rabbit hole). Roadmap: `kAXSelectedTextAttribute` insertion for native apps, character-typing fallback for apps that block ⌘V.

### 3.7 UI

- **`MenuBarExtra`** (SwiftUI, `.menu` style): state line, "Copy last dictation", Mode (Clean / Literal), Language (Auto / Português / English), Pause dictation, Model status, Settings…, Quit. Icon is a mic glyph that fills while listening; badge for token/secure-input problems.
- **HUD** — `NSPanel` with `.nonactivatingPanel`, `.floating` level, `ignoresMouseEvents = true`, `canBecomeKey = false`, `hidesOnDeactivate = false`, **`collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]`** **(review)** or it never shows over a full-screen Slack; positioned bottom-center of the screen that contains the mouse; appears 150 ms after Fn-down; SwiftUI content: state label + 24-bar waveform, progress for long clips; shows the cleaned text for 1.2 s on done **as a setting, default on** (it is visible in screen shares); single-line error strings (§6). Fades out. Non-activating is what keeps the paste from landing in our own window.
- **Settings** (`Settings` scene, tabs): General (hotkey, sounds, show-text-in-HUD, launch at login via `SMAppService`), Model (model picker, download/progress, delete, language default), Server (URL, token, "Test connection" → shows user name, sign out), Dictionary (list from server; add/remove), Permissions (Microphone / Input Monitoring / Accessibility with status dots and "Open System Settings" deep links, Fn usage check, Relaunch button).
- **Onboarding** on first launch: permissions → Fn setting → server token → model download → "try it: hold Fn and say hello" with a text field.
- Every user-facing string in `Strings.swift` so a pt-PT pass later is one file.

### 3.8 Storage on the Mac

- Keychain: device token (`kSecClassGenericPassword`, service `co.miraside.voice`, account = server URL).
- `UserDefaults`: hotkey, sounds, model id, server URL, and an offline cache of the server settings.
- Application Support/Voice: `dictionary.json`, `models/`. **No transcript ever touches disk**: no history, and the outbox is in memory.
- OSLog: every interpolation `privacy: .private`, and raw/cleaned text is never logged even so (Xcode's console reveals private data).

---

## 4. Backend design (`server/`)

**Stack:** Node 22, TypeScript strict, **Fastify 5** with `fastify-type-provider-zod` v5 (**needs `zod ≥ 3.25`**, not the hub's `^3.23.8` **(review)**), `@fastify/rate-limit`, `@fastify/helmet`, `pino`, **SQLite** via `better-sqlite3` (pinned to a version with linux-x64 glibc prebuilds; the slim image has no compiler, so a prebuild miss must fail the build loudly) + **Drizzle ORM**, `openai` SDK for Ollama Cloud. One process, one container, one volume.

Why SQLite: a handful of users writing a few hundred rows a day of text; WAL mode handles it, backups are a file copy, no second container competes with the ARwatches worker for RAM. Drizzle keeps the door open to Postgres.

### 4.1 Schema (`db/schema.ts`)

| table | columns |
|---|---|
| `users` | `id` (nanoid), `name`, `created_at` (ms), `disabled_at` (ms, null) |
| `device_tokens` | `id`, `user_id` →users, `token_hash` (sha256 hex, unique), `label` ("Miguel's MacBook"), `created_at`, `last_used_at`, `revoked_at` |
| `dictations` | `id`, `user_id`, **`client_id`** (UUID from the Mac; unique with `user_id`), `created_at`, `raw`, `cleaned` (null when literal/failed), `injected` (**null** until the client reports; then 'cleaned' \| 'raw' \| 'none' \| 'clipboard'), `mode`, **`language_setting`** (forced or 'auto'), **`language_detected`**, `app_bundle_id`, `app_name`, `audio_ms`, `asr_ms`, `llm_ms`, `asr_model`, `llm_model`, `client_version`, `fallback_reason` (null \| one of the enum in §6) |
| `user_settings` | `user_id` (pk), `json` (mode, language, hotkey, llm_model override), `updated_at` |
| `dictionary_entries` | `id`, `user_id` (null = team-wide), `term`, `replacement` (null = "spell exactly like this"), `note`, `created_at` |

Indexes: `dictations(user_id, created_at desc)`, unique `dictations(user_id, client_id)`, `device_tokens(token_hash)`, `dictionary_entries(user_id)`. Timestamps are ms epoch numbers (house convention).

### 4.2 Auth (`auth.ts`)

- Token format `mv_` + 32 random bytes base64url, shown **once** by the CLI. Stored as sha256. Lookup by hash; `last_used_at` written at most once per 5 minutes per token.
- Fastify `onRequest` hook decorates `request.user`; `/health` is the only public route.
- Rate limit 120 req/min **keyed by the token hash** (never the raw bearer) plus a per-IP limit on 401s so a scanner cannot fill the logs **(review)**. Body limit 64 KB (the API only ever carries text). Disabled users and revoked tokens → 401 `{error:'token_revoked'}` so the Mac shows the right message.

### 4.3 Refine (`llm/refine.ts`)

- Chat completion, non-streaming, `model` from settings override → `LLM_MODEL`, `reasoning_effort` from `LLM_REASONING` (`low` for gpt-oss; Ollama's endpoint accepts `none|low|medium|high|max`, not `minimal`, and `think:false` is rejected), `temperature: 0.1`, SDK `timeout = budgetMs − 700`, `maxRetries: 0`.
- **Token cap** **(review)**: `max_tokens = max(512, estTokens × 3 + 192)`. Ollama maps `max_tokens` to `num_predict`, which counts the reasoning channel too; gpt-oss emits tens to hundreds of analysis tokens even at `low`, so the naive `2× input + 64` cut short dictations off mid-reasoning and pasted raw for the majority of the day's dictations. `finish_reason === 'length'` → `fallback_reason: 'llm-truncated'`.
- **Concurrency** **(review)**: Ollama Cloud enforces a per-plan concurrency cap (Free 1, Pro 3, Max/Team 10) with queueing, then rejection, plus hourly/weekly caps returned as 429. One shared key means two teammates releasing Fn in the same second on Free/Pro queue behind each other. An in-process semaphore sized by `LLM_CONCURRENCY` (= plan tier) **fails fast** with `fallback_reason: 'llm-busy'` instead of queueing past the budget. The plan tier is confirmed on day 0 (Pro minimum for a team). The `/health` LLM probe is a `GET /v1/models` list call, never a completion, cached 60 s, and never fails the container.
- **Reasoning leakage** **(review)**: Ollama returns gpt-oss's analysis in `message.reasoning`, so `content` is normally clean; defensively strip any `<think>…</think>` block and reject if a `<think>` remains.
- **System prompt (docs/PROMPT.md is the source; summary):** you are a dictation cleanup engine; output only the cleaned text; keep the speaker's language and mixed pt/en as spoken; fix punctuation, casing and obvious ASR slips; remove fillers (um, uh, hã, ééé, "tipo" as filler), false starts and repeats; never add, remove or answer anything; never summarize; digits for numbers; lists only when the speaker enumerates; honour spoken commands *new line / nova linha, new paragraph / novo parágrafo*; spell these terms exactly: `{dictionary}`; return empty only for pure noise. `mode: literal` skips the LLM entirely.
- **Output guards (tested)** **(review)**: strip wrapping quotes/code fences and a leading "Here is…"-style preamble. Reject (→ raw, `guard-rejected`) when a line starts with "Cleaned text:", when the output exceeds `2.5 × rawLen + 20`, or, **only for inputs of 25+ chars**, when it is shorter than `0.3 × rawLen − 12` (so "Hã hã então olá" → "Olá." passes). **Noise** is defined as raw under 4 words with every token in the filler list: there an empty output is accepted and stored as `injected: 'none'`; for any other input an empty output is rejected. These cases are the guard test fixtures.
- **Bench (`cli/bench.ts`)** **(review)**: 20 fixture transcripts (pt, en, mixed, fillers, commands, a question, one-word) × models `gpt-oss:120b`, `gpt-oss:20b`, `gemma4`, `qwen3.5` × `reasoning_effort` `low` and `none` (gpt-oss may reject `none`; a non-reasoning model at `none` may beat gpt-oss at `low` for a rewrite). Prints p50/p95 latency, guard rejections, `usage.completion_tokens` minus content tokens (the real reasoning overhead), and a side-by-side. Then a **concurrency** test with 2, 3 and 5 requests in flight (a serial burst measures nothing on a queued API). The chosen default and numbers go into `docs/PROMPT.md`. `kimi-k3` is excluded: it bills against a separate "extra usage" balance on this account.

### 4.4 Routes and contract (`docs/API.md`)

All JSON, bearer auth, `X-Request-Id` echoed. Errors are `{ error: <snake_code>, message }`.

| Route | Request | Response |
|---|---|---|
| `GET /health` | — | `{ ok, version, llm: 'ok'\|'degraded', db: 'ok' }` — always 200 while the process is alive |
| `GET /v1/me` | — | `{ user:{id,name}, settings, dictionary:[{term,replacement}], server:{version, models:[…], concurrency} }` |
| `POST /v1/refine` | `{ clientId, raw, mode:'clean'\|'literal', languageSetting, languageDetected, budgetMs, context:{appBundleId?, appName?}, timing:{audioMs, asrMs}, asrModel, clientVersion, createdAt }` | `{ clientId, cleaned, raw, model, llmMs, fallbackReason: null\|'llm-timeout'\|'llm-error'\|'llm-busy'\|'llm-truncated'\|'guard-rejected' }` — on LLM failure the server still returns 200 with `cleaned = raw` and a reason; the row is upserted on `(user, clientId)` |
| `POST /v1/dictations` | `{ clientId, raw, injected, fallbackReason, mode, languageSetting, languageDetected, context, timing, createdAt }` | `{ id }` (outbox replay; upsert, never a duplicate) |
| `PATCH /v1/dictations/by-client/:clientId` | `{ injected }` | `{ ok }` |
| `GET /v1/dictations?limit=50&before=<ms>` | — | `{ items:[…], nextBefore }` |
| `GET /v1/settings` · `PUT /v1/settings` | `{ mode, language, hotkey, llmModel? }` | same |
| `GET /v1/dictionary` · `POST /v1/dictionary` · `DELETE /v1/dictionary/:id` | `{ term, replacement?, note?, teamWide? }` | entry |

### 4.5 Logging and observability

- pino JSON to stdout; per request: user id, route, status, ms, and for refine: `rawChars`, `cleanedChars`, `llmMs`, `model`, `fallbackReason`, `llmErrorKind`, `reasoningTokens`. **Transcript text is never logged** unless `LOG_TRANSCRIPTS=1`, which `env.ts` refuses in `NODE_ENV=production`. pino's `err` serializer carries the provider's error string, never the prompt.
- `docker compose logs -f voice-api` is the dashboard for v1.

### 4.6 Env (`server/.env.example`)

```
# ─── LLM (Ollama Cloud through its OpenAI-compatible endpoint) ───────────────
LLM_BASE_URL=https://ollama.com/v1
LLM_API_KEY=
LLM_MODEL=gpt-oss:120b          # default until the bench in docs/PROMPT.md says otherwise
LLM_REASONING=low               # none|low|medium|high; gpt-oss needs low, non-reasoning models take none
LLM_CONCURRENCY=3               # = Ollama plan tier (Free 1, Pro 3, Max/Team 10); extra requests fail fast
LLM_MAX_TIMEOUT_MS=14000        # ceiling for the per-request budget the Mac sends
# ─── Storage ─────────────────────────────────────────────────────────────────
DATABASE_PATH=/data/voice.db
# ─── Server ──────────────────────────────────────────────────────────────────
PORT=8080
LOG_TRANSCRIPTS=0               # refused in production
```

---

## 5. Deployment (`deploy/`)

**Audited 2026-09-09 (docs/SPIKES.md):** the VPS is Ubuntu 24.04, **2 vCPU / 7.8 GB**, root shell, and it already has a public reverse proxy: **Traefik** (`<proxy-container>`, docker provider, `exposedbydefault=false`, entrypoints `web`→`websecure`, cert resolver `mytlschallenge`, network `<proxy-network>`) owns 80/443 and ufw already allows them. ARwatches publishes only on `127.0.0.1`. Miraside deployments live under `/opt/miraside/`.

- **No Caddy, no new ports.** `voice-api` joins the external `<proxy-network>` network and carries Traefik labels (`Host(\`voice.miraside.co\`)`, `websecure`, `tls.certresolver=mytlschallenge`, service port 8080). Traefik issues the certificate on first request once the Namecheap A record `voice → <VPS_IP>` resolves.
- **Compose project `miraside-voice`** at `/opt/miraside/voice/deploy`, its own default network plus `edge` (= `<proxy-network>`), never touching the n8n, ARwatches or deal-pipeline compose files. Services: `voice-api` (built from `server/Dockerfile`, `node:22-bookworm` builder → `node:22-bookworm-slim` runtime; volume `voice-data:/data`; healthcheck `node -e "fetch('http://127.0.0.1:8080/health')…"` because the slim image has no curl; `restart: unless-stopped`; memory limit 512 MB on a 7.8 GB box), `backup` (alpine + sqlite3, cron `0 3 * * *` → `sqlite3 /data/voice.db ".backup /backups/voice-$$(date +%F).db"` — double `$$` inside compose YAML — keep 14).
- **VPS.md steps:** DNS A record → `git clone` into `/opt/miraside/voice` → `.env` → `docker compose up -d --build` → `docker compose exec voice-api node dist/cli/users.js add "Miguel" --label "MacBook"` → token → `curl https://voice.miraside.co/health`. Rollback = `git checkout <prev> && docker compose up -d --build`. Restore = stop, copy backup over `voice.db`, start. Port audit (`docker ps --format '{{.Names}}\t{{.Ports}}'`, only Traefik on `0.0.0.0`) on every deploy, because Docker bypasses ufw.
- Deploy is manual (`ssh -i ~/.ssh/<SSH_KEY> vps`, `git pull`, `compose up --build`) in v1; a GitHub Action over SSH is roadmap.

---

## 6. Error handling, timeouts, and what the user sees

One fallback enum everywhere (Mac, API, DB): `client-timeout | offline | unauthorized | server | llm-timeout | llm-error | llm-busy | llm-truncated | guard-rejected`.

| Situation | Behaviour | HUD |
|---|---|---|
| Fn held < 400 ms or silence | cancel, no request | "Nothing heard" (0.8 s) |
| Fn+arrow / Fn+F-key (keyDown while holding) | cancel, discard audio; HUD never showed (150 ms delay) | none |
| Esc while listening | cancel | "Cancelled" |
| Model not loaded yet | ignore Fn, badge on icon | "Model loading 42 %" |
| Whisper returns empty | no request | "Nothing heard" |
| Refine past budget / offline / 5xx | paste raw, outbox entry | "Pasted raw — server slow/offline" |
| Clip > 30 s | chunked transcription with progress | "Transcribing 40 %" |
| Refine still running after 3 s | keep waiting until budget | "Cleaning…" |
| LLM timeout / error / busy / truncated on server | 200 with `cleaned=raw` + reason | (nothing; text pasted) |
| Guard rejects LLM output | server returns raw + `guard-rejected` | (nothing) |
| Pure noise | server returns empty, `injected: none` | "Nothing heard" |
| 401 | paste raw, badge, Settings › Server | "Token invalid — open Settings" |
| Secure input active | text to clipboard only | "Secure field — copied instead" |
| Pasting app never reads the clipboard | restore after 1.5 s ceiling | — |
| Recording > 90 s | auto-stop and process | countdown in last 15 s |

Retries: none on `/v1/refine` (latency budget), one retry with backoff on outbox and settings calls.

---

## 7. Security and privacy

- Audio never leaves the Mac and never touches disk; transcripts never touch the Mac's disk either (in-memory outbox, no local history). Text goes to the VPS over TLS and from there to Ollama Cloud (confirm Ollama's cloud data-retention statement before rollout and record it in README).
- Ollama key lives only in the VPS `.env`. Macs hold a per-person revocable token in Keychain.
- The event tap never listens to keystrokes while idle (§3.2); README states it.
- Clipboard writes are `.currentHostOnly` so Universal Clipboard never carries a transcript to another device; the HUD text flash is a setting.
- Server: body limit 64 KB, rate limit by token hash + per-IP on 401s, helmet headers, no CORS (no browser client in v1), tokens hashed, no transcript logging in production.
- VPS: only Traefik listens on 80/443 (already the case); `voice-api` binds to the compose networks, never the host; the port audit runs on every deploy because Docker bypasses ufw.
- Secure-input detection prevents pasting into password fields.

---

## 8. Milestones (ordered; each ends with something runnable)

**Day 0 — Spikes (½–1 day, throwaway code, results in `docs/SPIKES.md`)** **(review)**
- WhisperKit on this M2: download `turbo_632MB`, time first-load specialization, memory, and transcription latency for the 3 fixtures; this number sets M3's acceptance and the §1 budget.
- Event tap: does Fn `flagsChanged` arrive while Terminal's Secure Keyboard Entry is on / a Safari password field is focused? Does ⌘V via CGEvent paste into Chrome, VS Code, Slack, Terminal?
- Ollama Cloud with the existing key: plan tier and concurrency cap; `reasoning` token overhead per model on 5 short transcripts; whether `gpt-oss:20b` and `reasoning_effort: none` are accepted.
- VPS: port audit + is there a public IP (decides Caddy vs Cloudflare Tunnel).

**M0 — Skeleton + live health check (½ day)**
- `hub/miraside-voice` repo (`git init`), README, AGENTS.md, docs; this plan copied to `docs/PLAN.md`.
- `server/`: Fastify hello + `/health`, Dockerfile, `deploy/` compose + Caddyfile (or tunnel) + VPS.md.
- Deploy; `https://voice.miraside.co/health` returns `{ok:true}` over TLS. ARwatches containers untouched. Nothing else depends on this until M4, so it can slip without cost.

**M1 — Backend complete (1.5 days)**
- Drizzle schema + migrations, `users` CLI, bearer auth, `/v1/me`, `/v1/refine` with semaphore + token cap + guards + `<think>` strip, dictations upsert by `clientId`, settings/dictionary routes, pino, tests (auth, guards incl. the short-input and noise cases, upsert idempotency, route contract with mocked LLM), `bench.ts` run (models × reasoning × concurrency) and results written to `docs/PROMPT.md`.
- Acceptance: filler-laden pt transcript cleaned in < 2 s p50; the same `clientId` posted twice yields one row; revoke → 401; 3 concurrent refines on a Pro key all succeed and a 4th fails fast with `llm-busy`.

**M2 — Mac shell: hotkey → paste "hello" (2.5 days)**
- Xcode project (Swift 5 mode, LSUIElement), menu bar, onboarding with the three permissions + Fn usage check + Relaunch, `HotkeyMonitor` with mask switching and tap re-enable, `AudioRecorder` with ring buffer + pre-started engine + meter, HUD (full-screen aware, 150 ms delay), `TextInjector` with data-provider-driven restore, secure-input check. On Fn release it pastes a fixed string.
- Acceptance: holding Fn in Mail, Notes, Slack, Chrome, VS Code, Terminal, Xcode, a full-screen app and a Finder rename field pastes at the caret; a previously copied image is still on the clipboard afterwards; Fn+arrow does not trigger and does not flash the HUD; TCC prompts appear once and survive rebuilds; Fn-down→first-buffer logged.

**M3 — On-device ASR (1 day)**
- WhisperKit package, `ModelManager` (download/progress/prewarm/delete), `WhisperKitTranscriber` with the §3.4 options, VAD + hallucination thresholds, chunking + progress, language auto/pt/en, dictionary prompt. Raw text gets pasted.
- Acceptance: 8 s pt-PT sentence pasted within the day-0 number + 0.3 s; silence fixture → "Nothing heard"; en fixture detected as en; a 60 s clip shows progress.

**M4 — Full loop with the backend (1.5 days)**
- `VoiceAPI`, `RefineService` with scaled budget + fallback + FIFO, Keychain token, Settings › Server with "Test connection", `/v1/me` sync with server-wins rule, `DictionaryCache`, PATCH by clientId, in-memory outbox replay, Mode + Language in the menu, literal mode.
- Acceptance: end-to-end dictation pastes cleaned text within budget; kill the server → raw pasted with the notice and the outbox replays when it is back, with no duplicate rows; two dictations in quick succession paste in order.

**M5 — Polish + teammate rollout (1 day)**
- Sounds, Esc/keyDown cancel, launch at login, 90 s cap, Strings enum, Copy-last-dictation, HUD-text setting, README install steps (build from source, or a zipped `.app` with an **empty entitlements file and `embedded.provisionprofile` stripped** **(review)**, then `xattr -d com.apple.quarantine` and *Privacy & Security → Open Anyway*, which macOS 15+ requires for unnotarized apps — tested on a second Mac **before** this milestone), issue tokens for the team.
- Acceptance: a second teammate dictates from their own token; history shows both users.

Total ≈ **8–10 focused days** for one developer (the review corrected the earlier 6–7). TDD applies to the pure parts (server modules, Swift state machine/FIFO/guards/pasteboard/budget); the OS-integration parts (TCC, event taps, paste across apps) are verified by the manual checklist below because they cannot run headless.

---

## 9. Verification

**Server (local):**
```
cd server && npm ci && npm run typecheck && npm test
LLM_API_KEY=… npm run dev            # http://localhost:8080/health
node dist/cli/users.js add "Miguel"   # prints mv_… once
curl -s -X POST localhost:8080/v1/refine -H "Authorization: Bearer mv_…" -H 'content-type: application/json' \
  -d '{"clientId":"11111111-1111-4111-8111-111111111111","raw":"hã então eu acho que que amanhã vamos vamos fechar o contrato tipo às três","mode":"clean","languageSetting":"auto","languageDetected":"pt","budgetMs":4000,"context":{},"timing":{"audioMs":6000,"asrMs":1400},"asrModel":"large-v3-turbo-632MB","clientVersion":"0.1.0","createdAt":1757400000000}'
# run it twice: one row in the DB
npm run bench                         # models × reasoning × concurrency table
```

**Server (VPS):** `docker ps --format '{{.Names}} {{.Ports}}'` (nothing but caddy on 0.0.0.0), `docker compose ps` (healthy), `curl https://voice.miraside.co/health`, `docker compose logs -f voice-api` while dictating, `ls /backups` next morning.

**Mac (automated):** `xcodebuild test -scheme Voice` runs the XCTest targets (state machine + FIFO ordering, pasteboard snapshot round-trip with multiple types and a promised type skipped, hotkey interpretation incl. the Fn+key cancel rule and tap re-enable, budget formula, refine fallback with a stubbed API, guard/normalization helpers). The WhisperKit fixture test is opt-in via `VOICE_ASR_TESTS=1` because it downloads the model.

**Mac (manual checklist, kept in README):**
1. Fresh install: all three permission prompts appear once; Fn usage warning shows when set to emoji; model downloads with progress and the menu reports ready.
2. Paste lands at the caret in Mail, Notes, Slack, Chrome (Gmail), VS Code, Terminal, Xcode, a full-screen app, and a Finder rename field; the previously copied image is still on the clipboard afterwards; nothing appears on the iPhone's clipboard.
3. Password field in Safari and Terminal with Secure Keyboard Entry → no paste, HUD notice or badge.
4. Fn+F5 and Fn+← do not dictate and do not flash the HUD; Esc cancels; a 200 ms tap does nothing.
5. Server down → raw pasted with notice; server back → outbox replays; the DB shows one row per dictation with `fallback_reason=offline`.
6. Invalid token → HUD message, badge, Settings opens on the Server tab.
7. Two dictations back-to-back (second Fn while the first is "Cleaning…") paste in order.
8. Numbers: log line per dictation shows `asrMs` and `llmMs`; ten dictations p50 within the §1 budget.

---

## 10. Roadmap after v1 (not in scope now)

- **Streaming ASR** while the key is held (WhisperKit streaming), so release-to-paste is mostly the LLM hop; **streaming paste** for long dictations.
- **Modes:** email / chat / literal, per-app defaults keyed by bundle id; snippets ("insert my signature"); toggle (hands-free) hotkey mode.
- **Injection v2:** AX `kAXSelectedTextAttribute` insertion and smart spacing for native apps, character-typing fallback for apps that block ⌘V.
- **Remote ASR providers:** VPS `faster-whisper` container (for Intel Macs or when the local model is not downloaded), Groq; **Apple SpeechAnalyzer** (macOS 26, zero download; check pt-PT); **Parakeet v3** via FluidAudio.
- **Web dashboard** for history/dictionary/users in the product design system (`hub/miraside/docs/product-design-system.md`), possibly as a Multi-Zone in `miraside-dashboard`; **Better Auth** sign-in replacing pasted tokens.
- **Distribution:** Developer ID + notarization (`notarytool`) + Sparkle auto-updates once an Apple Developer account exists; GitHub Action deploy over SSH.
- **Cost/usage view:** Ollama Cloud spend and 429s per user; encrypted on-disk outbox if outage gaps in history turn out to matter.

---

## 11. Things to confirm at implementation time

- Exact WhisperKit model folder names in `argmaxinc/whisperkit-coreml` (`turbo_632MB` vs `turbo`) and the current `DecodingOptions` field names in the pinned package version.
- Ollama plan tier for the shared key (sets `LLM_CONCURRENCY`), whether `gpt-oss:20b` is served, and whether `reasoning_effort: none` is accepted per model.
- Subdomain name (`voice.miraside.co` assumed), VPS OS / firewall tool (`ufw` assumed), public IP vs NAT (Caddy vs Cloudflare Tunnel).
- Ollama Cloud's data-retention statement for the README privacy note.
- Whether Fn events reach the tap under secure input (day-0 spike), which decides between the HUD notice and the idle poll + badge.
- The zipped-app path on a teammate's Mac (Open Anyway, provisioning profile stripped), tried once before M5.

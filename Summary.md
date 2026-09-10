# Summary — Miraside Voice

Miraside Voice is the in-house replacement for Wispr Flow: hold **Fn**, speak, release, and the
cleaned-up text is pasted into whatever app is in front. Speech-to-text runs **on the Mac**
(WhisperKit, free and ~1 s), the cleanup runs on **Ollama Cloud** (`gemma4`) through a small API on
the Miraside VPS, and only text is ever stored — audio never leaves the machine and is dropped the
moment it is transcribed.

Everything below was implemented, reviewed and tested in one autonomous session on 2026-09-09
(plan → two implementation plans → subagent-driven execution with a review after every task and a
whole-branch review at the end). The step-by-step to start using it is in `GO_LIVE.md`.

## 1. Architecture at a glance

```
Fn down ─► event tap (listen-only) ─► AudioRecorder (16 kHz ring buffer, 90 s cap)
Fn up   ─► EnergyGate (silence → "Nothing heard")
        ─► WhisperKit large-v3-turbo on-device  ≈0.9–1.6 s          [mac/Voice/ASR]
        ─► POST https://voice.miraside.co/v1/refine  (budget 3.5–15 s) [server/src/routes/refine.ts]
              └─ Ollama Cloud gemma4 (reasoning off) ≈0.6 s p50, guarded; on any failure → raw text
        ─► TextInjector: clipboard swap → ⌘V → clipboard restored      [mac/Voice/Inject]
        ─► PATCH /v1/dictations/by-client/:id  (what was pasted)   ; offline → in-memory outbox → replay
HUD (non-activating NSPanel) appears 150 ms after Fn-down; menu bar icon; Settings window.
Storage: SQLite on the VPS (text, timing, app context, fallback reason) — audio: never.
```

Key numbers (all measured on this M2 Mac and the real Ollama account, `docs/SPIKES.md`,
`docs/PROMPT.md`):

| what | number |
|---|---|
| on-device ASR, 12.5 s English clip | 0.92 s (spike), 1.3–1.6 s (in-app test, quiet Mac) |
| model first load (download + CoreML compile) | ~4.5 min once; ~10 s warm |
| LLM cleanup `gemma4`, reasoning off | p50 563 ms, 0 guard rejections on the bench set |
| Ollama Cloud concurrency | ≥5 parallel requests fine |
| server tests | 47 passing |
| Mac unit tests | 39 passing + 1 opt-in real-model test (`VOICE_ASR_TESTS=1`) |

## 2. What was built — server (`server/`, merged to `main` at a955ad7, deployed)

Node 22.22 + Fastify 5 + SQLite (better-sqlite3 + drizzle, hand-written migrations), TypeScript,
Docker. Contract in `docs/API.md`; prompt and bench in `docs/PROMPT.md`.

- **Auth**: per-device bearer tokens (`mv_` + 32 random bytes, only the SHA-256 is stored),
  `users` CLI (`add | list | token | revoke`). Authentication runs first; a failed-auth limiter
  (30/min per client IP → 429) only counts failures, so a scanner can never lock real users out.
  Client IP comes from Traefik's `X-Forwarded-For` (`trustProxy: 'loopback,uniquelocal'`).
- **Rate limit**: 120 req/min per token (`x-ratelimit-*` headers).
- **`POST /v1/refine`**: budget `clamp(2500 + 12×chars, 3500, 15000)` ms, server LLM timeout
  = budget − 700 ms (cap 14 s), concurrency semaphore, `max_tokens` sized from the input, and
  output guards (`<think>` leak strip, preamble detection, length-ratio bounds, noise = < 4 filler
  words). Any failure still answers 200 with `cleaned = raw` and a `fallbackReason`
  (`client-timeout | offline | unauthorized | server | llm-timeout | llm-error | llm-busy |
  llm-truncated | guard-rejected`). Rows upsert on `(user, clientId)`, so a retry never duplicates.
- **`/v1/dictations`** (POST for outbox replay, GET paginated newest-first, PATCH by client id),
  **`/v1/me`** (user, settings, dictionary, server model + allowed models), **`/v1/settings`**
  (mode, language, hotkey, `llmModel` validated against `LLM_ALLOWED_MODELS`), **`/v1/dictionary`**
  (personal + team-wide terms fed to both Whisper and the prompt), **`/health`** (public, LLM probe
  cached 60 s).
- **Privacy**: transcripts are never logged (`LOG_TRANSCRIPTS` is checked at boot), 500s return
  `{error:'server', message:'Internal error'}` only, helmet on, DB closed on shutdown so the WAL is
  checkpointed.
- **Tests**: 47 (`server/test/*.test.ts`) — auth, limiter, refine pipeline + guards, routes,
  pagination, settings validation, env parsing, migrations, error handler.
- **Deploy** (`deploy/`): `docker-compose.yml` with the API behind the VPS's **existing Traefik**
  (`<proxy-network>` network, `mytlschallenge` certresolver — no new ports, no Caddy), JSON log
  rotation, a `backup` sidecar (`sqlite3 .backup` nightly at 03:00, 14-day retention). Runbook
  `deploy/VPS.md` (§1–§9: access, port audit, DNS, deploy by rsync today / git later, verify, users,
  update/rollback, backup/restore, do-not-touch list).
- **State on the VPS right now** (`vps`, <VPS_IP>, `/opt/miraside/voice`): both containers
  up, `/health` → `{"ok":true,"version":"0.1.0","db":"ok","llm":"ok"}`; the Ollama key is in
  `deploy/.env` on the box only. **HTTPS is waiting on one DNS record** (GO_LIVE step 1). Your
  device token was minted there and stored in this Mac's login Keychain (service
  `co.miraside.voice`, account `https://voice.miraside.co`); it was never printed or written to disk.

## 3. What was built — Mac app (`mac/`, branch `feat/mac-v1`, merged to `main` at 9e8e9fc)

Swift 5 / SwiftUI, macOS 15, XcodeGen (`mac/project.yml`), WhisperKit 0.18 (`argmax-oss-swift`),
no App Store, ad-hoc signed for now. Bundle id `co.miraside.voice`, menu-bar only (`LSUIElement`).

| task | what | where |
|---|---|---|
| 1 | XcodeGen project, menu bar shell, ad-hoc signing, empty entitlements | `project.yml`, `Voice/App/VoiceApp.swift` |
| 2 | `DictationMachine`: pure reducer (idle → listening → transcribing → refining → inserting), ordered burst queue so back-to-back dictations paste in order | `Voice/App/DictationMachine.swift` |
| 3 | Hotkey: `HotkeyInterpreter` (Fn / right-⌥ hold, Esc cancels, arrows ignored), listen-only `CGEventTap` (`flagsChanged` while idle, `keyDown` added only while listening), permission helpers | `Voice/Hotkey/*`, `Voice/Support/Permissions.swift` |
| 4 | `AudioRecorder` (AVAudioEngine → 16 kHz mono ring buffer, 90 s cap, 400 ms minimum), `EnergyGate` (drops silence so Whisper cannot hallucinate "you") | `Voice/Audio/*` |
| 5 | `TextInjector`: snapshot the clipboard, put the text on it with transient/concealed markers, ⌘V, restore the clipboard **after the paste was read** (data-provider callback), secure-input guard → clipboard-only | `Voice/Inject/*` |
| 6 | HUD (non-activating `NSPanel`, 150 ms reveal delay, level meter, progress, messages), `Coordinator` wiring, onboarding window for the three permissions, sounds, `Preferences`, `Strings` | `Voice/UI/*`, `Voice/App/Coordinator.swift`, `Voice/Storage/*` |
| 7 | `WhisperKitTranscriber` (large-v3-turbo, language auto-detect, dictionary terms as prompt, VAD chunking + progress for > 30 s), `ModelManager` (download to `~/Library/Application Support/Voice/models`, completeness check), opt-in real-model test (`VOICE_ASR_TESTS=1`) | `Voice/ASR/*`, `VoiceTests/WhisperKitTranscriberTests.swift` |
| 8 | `VoiceAPI` (typed mirror of `docs/API.md`, status-only error mapping, `X-Request-Id`, token read off the main actor), `Budget`, in-memory `Outbox`, `RefineService` (budgeted refine → raw fallback → outbox → replay; literal mode logs only), `SyncService` (settings + dictionary pull every 10 min, merged push that never clears `llmModel`, server hotkey change re-creates the tap), `Keychain` (read-only at runtime), menu Mode/Language pickers and the unauthorized badge | `Voice/Refine/*`, `Voice/Storage/Keychain.swift`, `Voice/Storage/DictionaryCache.swift` |
| 9 | Settings window: General (sounds, HUD text, launch at login via `SMAppService`, mode, language), Hotkey (Fn / right ⌥ / right ⌘, deferred while a key is held), Model (pick, download, delete → reload), Server (URL, token → Keychain, test connection, sign out), Dictionary (list/add/delete team or personal terms), Permissions | `Voice/UI/SettingsView.swift`, dictionary calls in `Voice/Refine/VoiceAPI.swift` |
| 10 | `package.sh` (Release build → strip provisioning profile → sign ad-hoc or `VOICE_SIGN_IDENTITY` → `mac/build/Voice-<version>.zip`, 3.8 MB), repo-root `README.md` for teammates (install from source or zip, first run, event-tap and privacy posture, manual checklist, server, development) | `mac/scripts/package.sh`, `README.md` |

**Review coverage.** Every task went through an independent spec-and-quality review and a scoped
re-review of its fix round (tasks 6, 7, 8, 9 and 10 each needed one round). The additional
whole-branch review that closes each plan was started twice and stopped at your request, so the
branch was merged on the strength of the per-task reviews plus the full unit suite. Running that
cross-cutting pass later is listed in `GO_LIVE.md` step 9 — it is the one review seat this branch
did not get.

Things found and fixed along the way that were not in the plan: WhisperKit 0.18's `transcribe`
overloads bind a trailing closure to a deprecated overload (call with `callback:`); a non-empty
`promptTokens` with `temperatureFallbackCount: 0` returns empty text on real speech (the transcriber
retries once without the vocabulary prompt); `TEST_HOST`-launched tests do not inherit the shell
environment (scheme passes `VOICE_ASR_TESTS` through); `@AppStorage` inside an `ObservableObject`
never publishes (Preferences became a static namespace); `.onAppear` on a `MenuBarExtra` only fires
when the menu opens (the app delegate starts the coordinator).

## 4. Docs

- `docs/PLAN.md` — the approved design (architecture, budgets, privacy, risks).
- `docs/SPIKES.md` — day-0 measurements: Ollama Cloud latency/guards/concurrency, WhisperKit
  timings and model layout, VPS audit (Traefik, ports, resources).
- `docs/PROMPT.md` — the cleanup prompt, mode/language rules, bench results per model.
- `docs/API.md` — the client/server contract (every route, body, error shape, fallback enum).
- `docs/superpowers/plans/2026-09-09-voice-server.md`, `…-voice-mac.md` — the two implementation
  plans that were executed.
- `deploy/VPS.md` — operations runbook. `README.md` (repo root) — install, manual checklist, privacy notes.

## 5. How to run things

```bash
# server tests (Node 22.22.2 — 22.11 segfaults on better-sqlite3)
cd server && PATH=$HOME/.nvm/versions/node/v22.22.2/bin:$PATH npm test
# mac tests
cd mac && xcodegen generate && xcodebuild test -project Voice.xcodeproj -scheme Voice -destination 'platform=macOS' -quiet
# deploy (rsync today; deploy/VPS.md §4a has the exact three commands)
```

## 6. Rulings I made (decisions taken without asking, all reversible)

Server plan
- Work in place on `feat/server-v1` (no worktree): the folder is the app's home in `hub/`.
- Deploy behind the VPS's existing Traefik instead of adding Caddy — Traefik already owns 80/443.
- `/v1/me` exposes `server.model` (string) + `server.allowedModels`; `docs/API.md` follows the code.
- Plan bugs fixed before dispatch: semaphore `tryAcquire` form, migration table-list assert, the
  `:memory:` re-open test, `isNoise` handled before the LLM (not inside `refineText`).
- Final-review Criticals fixed in one wave: the per-token rate limit was never attached (routes were
  declared before the async plugin) → routes inside `app.after`; the failed-auth limiter ran before
  authentication with no `trustProxy`, so behind Traefik 30 bad requests locked everyone out →
  authenticate first, `trustProxy: 'loopback,uniquelocal'` (Fastify 5.12 ignores numeric hop
  counts; an `<proxy-network>` container could spoof XFF, but it can no longer lock anyone out); the
  restore runbook silently replayed the WAL → delete `-wal/-shm` before copying, DB closed on
  shutdown. `llmModel` validated against `LLM_ALLOWED_MODELS` (default
  `gemma4,gpt-oss:120b,qwen3.5`) instead of exposing LLM error kinds. Pagination stays
  `before < createdAt` with unique-ms `createdAt` per client (keyset deferred).
- Merged `feat/server-v1` into `main` locally with `--no-ff` (a955ad7) without the usual "merge /
  PR / keep" question, because the repo has no remote and you asked for autonomous completion; the
  branch is kept. Undo: `git reset --hard a955ad7~1` on `main`.

Mac plan
- Ad-hoc signing (`CODE_SIGN_IDENTITY "-"`): this Mac has no Apple Development identity, so
  unattended builds/tests needed it. Cost: TCC re-prompts after every rebuild until GO_LIVE step 4.
- Everything that needs a microphone, a permission grant or a live paste is a manual check for you
  (GO_LIVE step 3), never attempted from a script (no synthetic CGEvents, no TCC prompts).
- `Preferences` is a static namespace over `UserDefaults` (views bind with `@AppStorage`);
  `Coordinator.shared` is started from the app delegate; the onboarding window is owned by the
  delegate.
- Task 3 review: `setChoice` while the key is held must not orphan the press → Task 9 defers the
  change until release. `EventTap.stop()` mach-port invalidation deferred (slow leak, one tap per
  dictation).
- Task 4 review: input-format-captured-once (AirPods switch) deferred to your hardware pass;
  main-queue precondition added to the recorder.
- Task 5 review: re-entrant `insert()` unreachable but guarded anyway.
- Task 6 review: HUD could show before the 150 ms delay via the level callback → `panelShown`
  flag. Residual minor: restarting a burst while a "Done" panel is visible skips the delay.
- Task 7: progress from `windowId / ceil(total/30)` when chunking, indeterminate otherwise;
  model labels moved into `Strings`; `isDownloaded` requires `config.json` + the three core
  `.mlmodelc` bundles; the empty-text retry does not report progress; the main-actor-blocking
  concern was ruled a non-issue (nonisolated `async` methods run off the caller's actor).
- Task 8: the brief's offline outbox test could not pass as written → `StubAPI.offline` flag;
  the live loop against a local server is your GO_LIVE step 3, not something an implementer
  should run (it needs the key, a Keychain write and a `defaults write` on your prefs). Review
  found four defects, three inherited from the plan's own code, fixed in one round: a server-synced
  hotkey never re-created the event tap; literal dictations were posted with `fallbackReason:
  offline` instead of null; every settings push cleared the server's `llmModel` (and the menu's
  `.onChange` echoed pulled values back); the Keychain read ran on the main actor, where the macOS
  "allow access" dialog would have frozen the HUD. `Outbox`/`RefineService` are now `@MainActor`.
- Task 9: review found five defects, fixed in one round: the hotkey-change-while-held guard did not
  reset on Esc-cancel; the Model tab could not activate a second already-downloaded model and had no
  recovery after a failed model load (now: auto-reload on pick, an always-available "Reload model"
  button, Delete disabled for the active model); the token field kept the pasted token after Save;
  and my own touch list had pushed the implementer into duplicating the onboarding permission rows
  wholesale (now one shared `PermissionsView`). A failed model load still leaves the hotkey inert
  until a reload succeeds — accepted, the Settings button is the recovery path.
- Task 10: three README errors (no "Save token" click, a broken model-seeding command, the bare
  `ssh vps` form) were fixed and verified by me directly instead of a third review agent (a
  12-line documentation diff).
- Final review skipped at your request; merged `feat/mac-v1` into `main` with `--no-ff` (9e8e9fc),
  branch kept, nothing pushed (no remote). Undo: `git reset --hard 9e8e9fc~1` on `main`.

## 7. Deferred and known gaps (none blocking daily use)

- `voice.miraside.co` HTTPS: waiting for the Namecheap A record (GO_LIVE step 1).
- `/health` cannot detect a bad Ollama key (Ollama's `/v1/models` does not validate keys); a bad key
  shows up as `llm-error` fallbacks in `GET /v1/dictations`.
- Pagination can skip rows that share a `createdAt` ms (the client mints unique ones; keyset
  pagination on `(createdAt, id)` deferred). `users` CLI has no `disable`. Dictionary terms are not
  escaped in the prompt (trusted team).
- Mac: AirPods/device switch mid-session (recorder format captured once); event-tap mach port not
  invalidated on stop (tiny leak per dictation); burst-restart HUD delay residual; the
  `promptTokens` empty-text quirk in WhisperKit 0.18 costs one extra decode when it triggers;
  `pt-synthetic.wav` is not real Portuguese; Fn under secure input not yet verified on hardware
  (`docs/PLAN.md` §11).
- Not yet done: the whole-branch review (GO_LIVE step 9). Small items the per-task reviewers
  noted out of scope: `SyncService` keeps its last-synced settings across an `await` (a 10-minute
  timer racing a manual push could keep a stale copy for one cycle); the unit tests write the
  dictionary cache to `~/Library/Application Support/Voice/dictionary.json`; `OnboardingView` has an
  unused parameter; `Permissions.relaunch()` ignores a launch failure.

## 8. Repository state

```
hub/miraside-voice            (git root; NO remote yet — GO_LIVE step 7)
├── main                      a955ad7  server merged + 9e8e9fc mac merged  ← you are here
├── feat/server-v1            61f3ce7  kept for inspection
├── feat/mac-v1               a65a91e  kept for inspection
├── server/  deploy/  docs/  mac/
├── Summary.md  GO_LIVE.md    (this file and the next-steps file)
└── .superpowers/             (untracked review ledgers, briefs and reports — deleted after this summary)
```

Secrets: the Ollama key lives only in `deploy/.env` on the VPS (and `hub/miraside/.env.local`
where it came from); device tokens only in Keychains and as SHA-256 in the VPS database. `.env`
files are git-ignored. Nothing in git contains a secret.

## 9. Next steps

All in `GO_LIVE.md`, in order: DNS record → build & permissions → manual dictation checklist →
Apple ID signing → teammates' tokens → second Mac → git remote → first backup check.

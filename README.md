# Spit
<img width="2288" height="1160" alt="SL" src="https://github.com/user-attachments/assets/1d2c7621-5288-47dd-bd32-8ee7b3aec986" />



A Sintra Labs Open Source dictation app for macOS and Windows: hold a hotkey, speak, release, and clean text lands at the cursor. Speech is recognised on the device (WhisperKit on the Mac, whisper.cpp on Windows), text refinement and history live on the VPS, and nothing leaves the computer unencrypted.

## Architecture

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

**Why this split:** everything latency-critical (hotkey, audio, ASR, paste) runs locally and works offline. The backend owns the only shared secret (Ollama API key), the team's history, and the dictionary. The wire carries text only — audio never leaves the Mac, transcripts never land on disk.

## Status (2026-09-13)

Shipping to friends is built and verified as far as a machine without a person can take it, on branch `spit-mac-windows` ([pull request #1](https://github.com/mecls/spit/pull/1)). **Nothing is released yet**: no `v0.2.0` tag, no GitHub Release, no site deploy.

| Part | State |
|---|---|
| **Server** | Live on the VPS behind Traefik; HTTPS for `voice.miraside.co` is live. Unchanged by the Windows work (81/81 tests). |
| **Mac app** | Signed with the Apple Development identity (team `FZC6P6XRGD`); 143 unit tests, 1 opt-in ASR test skipped. `mac/scripts/package.sh` builds `Spit.dmg` (arm64, version 0.2.0) and `verify-dmg.sh` passes all 16 checks. Not notarised: macOS shows "Apple could not verify…" and friends use Open Anyway. |
| **Windows app** | New client in `windows/` (.NET 10, WPF), default model Whisper small (264 MB). 213 platform-neutral tests (108 of them the Mac's, ported by name) plus Windows-only tests. CI on a GitHub Windows runner builds the whole UI, pastes into a real Notepad, transcribes a fixture with the shipped model (12.9 s for 12.5 s of audio, no GPU), packs `Spit-Setup.exe`, and installs, runs and uninstalls it. **Never run on a physical PC.** |
| **`/spit` page** | Written at `SintraLabs/site/spit/index.html` and linked from the home page's Index — outside this repo, not deployed. |

### What's missing

In the order it should happen (details in `tasks/tasks-spit-mac-windows.md`):

1. **Run the Windows build on a real PC** — the hotkey, the microphone and the bar have never had a real key press or a real voice. Download `Spit-Setup.exe` from the latest `windows-ci` run's artifacts and run the spikes:
   - speed on your hardware;
   - Right Ctrl / Right Alt / AltGr on a pt-PT keyboard, and whether Right Alt opens menus;
   - pasting into Notepad run as administrator (CI runs elevated and cannot test it);
   - Clipboard History (Win+V) never keeping a dictation;
   - installing a newer build over an older one.
2. **Manual checklists** for Mac and Windows (`tasks/prd-spit-mac-windows.md` §5), with screenshots of the Windows SmartScreen and Edge warnings to fill the marked placeholder on the `/spit` page.
3. **Release 0.2.0:** merge PR #1 → push tag `v0.2.0` (CI drafts the release and attaches `Spit-Setup.exe`) → `mac/scripts/package.sh --release` (adds `Spit.dmg` and `SHA256SUMS.txt`) → check both downloads from the draft → publish. `release-windows.yml` has never run, because nothing has pushed a tag.
4. **Deploy `site/`** and confirm `/spit` resolves; the download buttons 404 until the release is published.
5. **Latency numbers** from 20 real Windows dictations (the SQL in the PRD §5).

Known limitations and open items:

- **Live transcription on Windows** (experimental, off by default) fell back to a full re-transcription on the CI clip: correct text, but 37 s instead of 12.9 s.
- **Smart App Control** on Windows 11 blocks the unsigned installer outright; accepted, and the page says so.
- **Admin windows:** the Windows hotkey and paste don't reach an app running as administrator (documented).
- **Open question:** a spending ceiling for friends' text cleanup on the Ollama key.

## Install for teammates

**Friends and teammates:** download `Spit.dmg` (Mac) or `Spit-Setup.exe` (Windows) from the `/spit` page on the Sintra Labs site, which walks through each OS's warning. Everything below is for building it yourself.

### A) From source (macOS 15+, Xcode 26)

```bash
git clone https://github.com/mecls/spit.git
cd spit/mac
brew install xcodegen
xcodegen generate
open Voice.xcodeproj
```

1. In Xcode, under Signing & Capabilities, set your team ID (or leave blank for ad-hoc).
2. Press ⌘R to build and run.
3. If prompted for Input Monitoring or Accessibility, grant it and the onboarding window will relaunch the app.
4. Open Settings › Server, paste your device token (issued on the VPS) → click **Save token** → click **Test connection** (the menu then shows your name).

The app runs ad-hoc by default. If you have an Apple Developer account, set `VOICE_SIGN_IDENTITY="Apple Development"` before building so the permissions grant persists across rebuilds.

### B) From a zip (macOS 15+)

1. Download `Spit-<version>.zip` from a colleague.
2. Unzip it and remove the quarantine attribute:
   ```bash
   unzip Spit-*.zip
   xattr -d com.apple.quarantine Spit.app
   ```
3. Move `Spit.app` to `/Applications`. If an older `Voice.app` is there, quit it and delete it first — it is the same app under its old name, and your token, model and settings carry over.
4. Double-click to launch. macOS 15+ will show **"Spit cannot be opened because it is from an unidentified developer"** — click **"Open Anyway"** (System Settings › Privacy & Security if the button does not appear).
5. Grant Input Monitoring and Accessibility when prompted (macOS will open System Settings).
6. Return to the app, open **Settings › Server**, paste your device token → click **Save token** → click **Test connection** (the menu then shows your name).

## First run

On first launch, the app downloads WhisperKit's `large-v3-v20240930_turbo_632MB` model (~630 MB) to `~/Library/Application Support/Voice/models/`. This takes 4–5 minutes the first time and ~10 seconds on warm starts.

To skip the download on a new Mac, transfer the model folder from a colleague via zip:

```bash
# on a Mac that already has the model:
cd ~/Library/Application\ Support/Voice/models && zip -r ~/Desktop/voice-model.zip models
# send voice-model.zip (AirDrop / Drive), then on the new Mac:
mkdir -p ~/Library/Application\ Support/Voice/models && cd ~/Library/Application\ Support/Voice/models && unzip ~/Desktop/voice-model.zip
```

The full model folder path is `~/Library/Application Support/Voice/models/models/argmaxinc/whisperkit-coreml/openai_whisper-large-v3-v20240930_turbo_632MB`.

## What the event tap sees

The app listens to your keyboard via a system event tap (required for the hotkey and for canceling with Escape). **While idle:** only Fn press/release (`flagsChanged` events). **While dictating:** Fn, Escape, and any key press (to cancel if Fn is accidentally held down with a real key like F5 or arrow keys).

**Privacy:** The tap never records keystrokes outside of dictation mode. Audio is never sent to the cloud — it stays on your Mac, processed by WhisperKit. Transcripts are never written to disk on the Mac; they flow from local Whisper → VPS for cleanup → erased from the outbox once sent to Ollama Cloud. The cleaned text lands in your app's clipboard, the previous clipboard is restored, and nothing syncs to iCloud. (HUD flash is optional in Settings › General.)

## Manual checklist

Tasks 1–8 below verify behavior that cannot be tested automatically. Run them before shipping to a new teammate:

- [ ] 1. Fresh install: all three permission prompts appear once; Fn usage warning shows when set to emoji; model downloads with progress and the menu reports ready.
- [ ] 2. Paste lands at the caret in Mail, Notes, Slack, Chrome (Gmail), VS Code, Terminal, Xcode, a full-screen app, and a Finder rename field; the previously copied image is still on the clipboard afterwards; nothing appears on the iPhone's clipboard.
- [ ] 3. Password field in Safari and Terminal with Secure Keyboard Entry → no paste, HUD notice or badge.
- [ ] 4. Fn+F5 and Fn+← do not dictate and do not flash the HUD; Esc cancels; a 200 ms tap does nothing.
- [ ] 5. Server down → raw pasted with notice; server back → outbox replays; the DB shows one row per dictation with `fallback_reason=offline`.
- [ ] 6. Invalid token → HUD message, badge, Settings opens on the Server tab.
- [ ] 7. Two dictations back-to-back (second Fn while the first is "Cleaning…") paste in order.
- [ ] 8. Numbers: log line per dictation shows `asrMs` and `llmMs`; ten dictations p50 within the §1 budget (~2–3 s).

**Second-Mac test:** once the checklist passes on one Mac, test the zipped app on a second teammate's Mac and verify the "Open Anyway" flow, permission prompts, and that both users appear in the server's user list with `last_used` timestamps.

## Server

The full operations runbook — deployment, backup, rollback, and user management — is kept
outside this repository (`deploy/VPS.md`, local only) because it contains host and SSH details.

**Issuing a token for a teammate:**

```bash
ssh -i ~/.ssh/<SSH_KEY> -o IdentitiesOnly=yes vps 'cd /opt/miraside/voice/deploy && \
  docker compose exec voice-api node dist/cli/users.js add "<name>" --label "<device>"'
```

The token is shown once and printed to stdout — copy it and give it to your teammate.

**See also:**
- [docs/API.md](docs/API.md) — the server contract (endpoints, request/response schemas, error codes).
- [docs/PLAN.md](docs/PLAN.md) — design decisions and latency budget breakdown.

## Development

**Server tests (Node 22.22 via .nvmrc):**
```bash
cd server
npm test
```

Runs 81 tests on auth, routes, LLM errors, and the refine budget.

**Mac tests:**
```bash
cd mac
xcodegen generate
xcodebuild test -project Voice.xcodeproj -scheme Voice -destination 'platform=macOS' -quiet
```

Runs 143 unit tests (1 opt-in ASR test skipped) on the state machine, FIFO ordering, clipboard snapshots, hotkey interpretation, and text guards. To also run the opt-in WhisperKit ASR test (downloads the model):
```bash
VOICE_ASR_TESTS=1 xcodebuild test ...
```

**Mac DMG:**
```bash
mac/scripts/package.sh             # tests, arm64 Release, signs, writes mac/build/Spit.dmg, runs verify-dmg.sh
mac/scripts/package.sh --release   # refuses ad-hoc signing; uploads Spit.dmg + SHA256SUMS.txt to the vX.Y.Z draft
```

The version lives in the root `VERSION` file; `scripts/check-version.sh` fails when `mac/project.yml` or a tag disagrees.

**Windows client (`windows/`, .NET 10):** `Spit.Core` holds the rules ported from the Mac and runs anywhere; `Spit.App` is the WPF shell and runs on Windows 10/11 x64.
```bash
dotnet test windows/Spit.Core.Tests        # the Mac's tests, ported by name (runs on macOS too)
windows/scripts/parity-check.sh            # fails if a ported test class drifts from its Swift file
dotnet build windows/Spit.sln              # compiles Spit.App on macOS as well (EnableWindowsTargeting)
pwsh windows/scripts/pack.ps1              # Windows only: self-contained publish + Velopack → windows/Releases/Spit-Setup.exe
```

CI (`.github/workflows/windows-ci.yml`) runs the core tests on Linux and Windows, the Windows-only tests, packs the installer, transcribes a fixture with the real model, and installs/uninstalls it on a Windows runner; the installer is attached to each run as an artifact. Pushing a `vX.Y.Z` tag runs `release-windows.yml`, which attaches `Spit-Setup.exe` to a draft release.

**Documentation:**
- [docs/PLAN.md](docs/PLAN.md) — architecture, design decisions, latency budget, verification checklist.
- [docs/API.md](docs/API.md) — server endpoint schemas and error handling.
- [docs/PROMPT.md](docs/PROMPT.md) — the refine prompt and model selection rationale.
- [docs/SPIKES.md](docs/SPIKES.md) — measurements: ASR latency (Mac and Windows), event tap behavior, Ollama tiers.
- [tasks/prd-spit-mac-windows.md](tasks/prd-spit-mac-windows.md) — the spec for shipping on Mac and Windows; [tasks/tasks-spit-mac-windows.md](tasks/tasks-spit-mac-windows.md) — its task list and what is still open; [tasks/spit-mac-windows-build-spec.md](tasks/spit-mac-windows-build-spec.md) — the build brief and every decision taken during the build (§16).

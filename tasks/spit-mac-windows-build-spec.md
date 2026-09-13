# Spit for Mac and Windows — Build Spec

**One-line mission:** Make Spit installable from a web page by Miguel's friends: a strictly signed
`Spit.dmg` for Apple Silicon Macs and a `Spit-Setup.exe` for a new Windows client that follows the Mac
app's rules, constants and tests, both talking to the existing server unchanged.

---

## 0. How to use this document

You are building this unattended. Nobody will answer questions while you work, so:

1. **This document outranks your instincts.** Where it names a rule, a number, or a stack
   choice, follow it even if you would have chosen differently.
2. **When you hit something genuinely unspecified, decide and keep moving.** Do not stall
   and do not invent scope. Pick the smallest choice consistent with §1 and §2, append it to
   the Decisions Log in §16 with one line of reasoning, and continue.
3. **§13 is your finish line.** Check your work against those scenarios rather than waiting
   for a human to confirm. Work is done when they pass, not when the code looks finished.
4. **The non-goals in §2 are binding.** If a change would be genuinely useful but sits
   outside them, note it in §16 as a suggestion and do not build it.

The source of *why* is `tasks/prd-spit-mac-windows.md` (rules R1–R46, spikes S1–S5). The step list
is `tasks/tasks-spit-mac-windows.md`; tick its boxes as you go. Where this document and the PRD
disagree, this one wins; note the disagreement in §16.

Environment facts that will otherwise cost you time:

- **You develop on a Mac with no Windows machine.** `Spit.Core` and its tests build and run here.
  `Spit.App` (WPF) *compiles* here with `-p:EnableWindowsTargeting=true` but cannot run. Everything
  that needs Windows at runtime is proven in GitHub Actions on `windows-latest`. Anything that needs a
  human at a real PC (spikes S2–S5 in full, the manual checklists, SmartScreen screenshots) is marked
  **(PC)** in the task list; build around it and never wait on it.
- **.NET 10 SDK 10.0.401 is in `~/.dotnet`.** Prefix every command with
  `export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1`. `/usr/local/share/dotnet` is SDK 6
  and must not be used.
- **The repo `mecls/spit` is public.** `GO_LIVE.md` and `deploy/VPS.md` are gitignored; never
  `git add -f` them. No secrets in any file or workflow.
- **Do not publish a GitHub Release, push a `v*` tag, deploy the server, or deploy the site.** Those are
  Miguel's (task 8.x). CI proves the `.exe` through workflow artifacts only.
- **Do not launch Spit.app on this Mac.** Miguel runs his own `mac/build/Spit.app`; a second instance
  fights it for the hotkey. Verify the DMG by inspection (§12).
- **Work on branch `spit-mac-windows`.** Commit in coherent steps; push; open the PR only at the end.

---

## 1. Primary user and outcome

**Primary user:** a friend of Miguel's, not a developer, who has been sent a hand-issued token and a
link to `/spit`, and owns either an Apple Silicon Mac on macOS 15+ or an x64 Windows 10/11 PC.

**Outcome:** they download one file, get past the OS warning by following the page, paste their token,
and hold a key to dictate text into whatever app they were using.

**Narrowing principle:** **Windows gets the Mac app's behaviour, not new behaviour.** Every constant
and rule comes from the Mac source named in R22; if a Windows feature has no Mac counterpart and no
rule in R27–R44, it is not built.

## 2. Non-goals

- **Notarisation and Windows code signing** — decided against; Smart App Control is open question 2.
- **Auto-update on either platform** — the Mac has none; Velopack's `UpdateManager` is not referenced.
- **Dictating into elevated (admin) windows** — needs UIAccess, which needs signing (R30).
- **Server or API changes** — the only server-side edit is one note in `docs/API.md` (R45, R46).
- **Intel Macs, Windows on ARM, Linux, stores, Homebrew, translations, telemetry** — PRD §6.
- **Changing Mac behaviour** — the Mac work is version, architecture and packaging only (task 2.0).
- **Publishing** — no release, no tag, no site deploy (§0).

## 3. Journeys

### Journey A — Friend installs and dictates on Windows (happy path)
1. Friend downloads `Spit-Setup.exe` and runs it. Velopack installs to `%LocalAppData%\Spit\` with no
   admin prompt, creates Start Menu and desktop shortcuts, and starts `Spit.exe`.
2. The Set-up window opens with three rows: Microphone, Hotkey ("Hold Right Ctrl now"), Token.
3. Friend opens Settings › Server, pastes the token, clicks Save, sees "Connected as *name*". The model
   `ggml-large-v3-turbo-q5_0.bin` (574 MB) downloads with progress in the bar.
4. Friend holds Right Ctrl in Notepad, speaks, releases. The bar shows the waveform, then
   "Transcribing…", then "Cleaning…", and the cleaned text appears at the caret. The bar returns to
   idle 1.2 s later. `POST /v1/dictations` carries `asrModel: "whisper.cpp/ggml-large-v3-turbo-q5_0"`.

### Journey B — Dictation into an admin window (unhappy path: elevated target)
1. Friend focuses Notepad run as administrator and holds Right Ctrl. Nothing happens: the hook cannot
   see keys in elevated windows (R30).
2. Friend clicks the bar's mic button, speaks, clicks it again.
3. At paste time the foreground process is elevated, so the text is not pasted. The bar shows
   "Admin window — text copied to clipboard" and the dictation is reported with `injected: "clipboard"`.
4. Friend presses Ctrl+V themselves; the text appears.

### Journey C — Offline and blocked microphone (unhappy path)
1. With the network off, friend dictates. Refine fails; raw text is pasted and the bar shows
   "Pasted raw — server slow or offline". The report goes to the in-memory outbox (cap 200).
2. Network returns; the next dictation flushes the outbox, and both appear in `GET /v1/dictations`.
3. Friend turns off microphone access in Windows privacy settings and holds Right Ctrl. The bar shows
   "Microphone blocked — Settings › Privacy & security › Microphone" with an Open button that launches
   `ms-settings:privacy-microphone`. Nothing is transcribed or sent.

### Journey D — Miguel builds the Mac DMG (happy path)
1. Miguel runs `mac/scripts/package.sh`. It reads `VERSION` (`0.2.0`), runs the tests, builds Release
   for arm64, signs with his Apple Development identity, and writes `mac/build/Spit.dmg`.
2. `mac/scripts/verify-dmg.sh` runs last and prints every check as PASS.
3. With `--release` and no identity, the script exits non-zero before building a DMG.

## 4. Screens and states

### Bar (`UI/BarWindow.xaml`)
- **Primary action:** show dictation state; click the mic to start/stop a hands-free dictation.
- **Secondary actions:** none.
- **Empty:** the resting lozenge (Mac `HUDView` idle metrics: 44×12).
- **Loading:** model not downloaded → "Model not downloaded"; downloading → percentage; loading →
  "Model loading". Hotkey presses do nothing in these states.
- **Error:** one line of status text from `Strings` (nothing heard, pasted raw, token invalid, admin
  window, clipboard busy, microphone blocked / unavailable), then idle after 1.2 s.
- **Success:** "Done" (and the pasted text when "Show text in the bar" is on), then idle after 1.2 s.

### Tray icon and menu (`UI/TrayIcon.cs`)
- **Primary action:** left-click opens the main window.
- **Secondary actions:** right-click menu in R37's order.
- **Empty:** plain mic icon; status line "Ready — hold Right Ctrl to dictate".
- **Loading:** status line shows the model state.
- **Error:** unauthorized → mic-with-cross icon and "Token invalid — open Settings".
- **Success:** listening → filled mic; latched → red record icon.

### Main window — Insights (`UI/InsightsView.xaml`)
- **Primary action:** read four cards (total words, WPM, streak + 21-week heatmap, apps).
- **Secondary actions:** Refresh.
- **Empty:** every figure `—` and "Dictations will appear here once you start using Spit."
- **Loading:** no cache → dimmed placeholders; cache present → cached numbers immediately.
- **Error:** cached numbers plus "Last updated *relative* · couldn't reach the server"; 401 → "Token
  invalid — open Settings › Server"; time-zone conversion failure → "Couldn't determine your time zone"
  and no request.
- **Success:** populated cards; `insights-cache.json` replaced.

### Main window — Settings (`UI/SettingsView.xaml`)
- **Primary action:** change a setting; it applies immediately.
- **Secondary actions:** Server: Save token, Test connection, Sign out. Model: Download, Delete, Reload.
  Dictionary: Add, Delete.
- **Empty:** no token → "Not connected"; no dictionary terms → "No dictionary terms yet".
- **Loading:** buttons disabled while their request runs; model download shows progress.
- **Error:** inline text under the control (`launchAtLoginError`, `modelDeleteError`,
  `dictionaryLoadError` etc.); a failed Launch-at-login write reverts the toggle.
- **Success:** "Connected as *name*"; the toggle reflects the registry value read on open.

### Set-up window (`UI/SetupWindow.xaml`)
- **Primary action:** Done (enabled when Microphone and Hotkey rows are green).
- **Secondary actions:** "Open microphone settings", "Open Settings › Server".
- **Empty:** all rows grey with their instructions.
- **Loading:** Token row "Checking…" while `/v1/me` runs.
- **Error:** Microphone "Blocked" with the settings button; Token "Token invalid".
- **Success:** each row green; Done closes the window and sets `onboarded`.

### `/spit` page (`../../site/spit/index.html`)
- **Primary action:** Download for Mac / Download for Windows (plain links to `latest/download`).
- **Secondary actions:** `SHA256SUMS.txt` link.
- **Empty / Loading:** static page; before the first release both links 404 — acceptable because the
  page is not deployed until task 8.4.
- **Error:** not applicable (no script-dependent content; JS only highlights the visitor's OS button).
- **Success:** every R18 statement present; the Windows warning copy is a clearly marked placeholder
  block until the (PC) screenshots exist.

## 5. Capabilities

- [ ] `VERSION` file and version plumbing on both platforms (R6)
- [ ] Mac DMG build with strict release signing, arm64, and a verification script (R12–R14)
- [ ] `Spit.Core`: reducer, gestures, key translation, gates, stream tail/stitch/policy, skip gate,
      budget, outbox, API client, refine, sync with hotkey echo, Insights, paste routing (R22, R23, R46)
- [ ] 108 ported tests with Mac names + R28, R43, R46 tests, and a parity check script
- [ ] `Spit.App` platform: keyboard hook, menu mask, capture, clipboard/paste, elevation, context,
      model manager, Whisper transcriber, streaming, token/settings/paths/launch-at-login, coordinator
- [ ] `Spit.App` UI: tray, bar, main window (Insights, Settings), Set-up, strings, sounds, single instance
- [ ] Smoke-test mode and Windows integration tests
- [ ] Velopack packing script; CI workflow (tests, pack, smoke, install/uninstall, artifact); tag-driven
      release workflow
- [ ] Docs (`docs/API.md`, `README.md`) and the `/spit` page

## 6. Invariants — enforce in code and prove in tests

1. **Same constants as the Mac.** Every value in PRD R22's table is a named constant in `Spit.Core`
   with the Mac name in a comment; `parity-check.sh` fails if any of the 14 ported test classes differs
   from its Swift file in test count or names.
2. **The hook never swallows a key.** Every `LowLevelKeyboardProc` path returns
   `CallNextHookEx`; the callback does no work beyond enqueueing (R29). Reason: a slow callback is
   silently removed by Windows, and swallowing Right Alt breaks @ and € on pt-PT keyboards.
3. **Three events are not "another key".** Autorepeat of the held hotkey, scan code `0x21D`, and any
   `LLKHF_INJECTED` event never produce `.cancel` (R28). Proven by `HotkeyTranslatorTests`.
4. **Restore never early.** The clipboard is restored no sooner than 1.5 s after the paste keystroke
   (R32). A dictation that cannot open the clipboard within 200 ms is not pasted and is reported
   `injected: "none"` (R33).
5. **No user speech or text on disk or in logs.** No WAV files, no transcript text in logs, outbox in
   memory, `dictionary.json` terms only, `insights-cache.json` numbers only; pasted clipboard content
   carries `ExcludeClipboardContentFromMonitorProcessing` (R25). Window titles are never read (R26).
6. **The PC never changes the Mac's hotkey.** `PUT /v1/settings` sends `hotkey` and `llmModel` exactly
   as last received from `GET /v1/me`; with no successful `/v1/me` this launch, no PUT is sent (R46).
7. **Time zone is IANA or nothing.** `TimeZoneInfo.TryConvertWindowsIdToIanaId` (or an id that is
   already IANA); on failure `/v1/insights` is not called. Never `UTC` (R43).
8. **Only a verified model is loaded.** Download to `<file>.partial`, SHA-256 must equal the pinned
   hash, then rename; the loader only opens final names (R41).
9. **One inference at a time per model instance** — a `SemaphoreSlim(1,1)` around every Whisper call;
   the tail pass waits for an in-flight stream pass (R42).
10. **Data outside the install dir.** Everything written lives in `%LOCALAPPDATA%\Miraside\Spit\`; the
    token lives in Credential Manager with `CRED_PERSIST_LOCAL_MACHINE` (R5, R44).
11. **One instance.** A second `Spit.exe` signals the first and exits 0 (R36).
12. **Release DMGs are never ad-hoc.** `package.sh --release` exits non-zero without an Apple
    Development identity or with a TeamIdentifier other than `FZC6P6XRGD` (R13).
13. **Mac identity unchanged.** Bundle id `co.miraside.voice`, module `Voice`, Keychain service and
    Application Support folder untouched (R3, R4).

## 7. Data model and lifecycle

### Local settings (`settings.json`)
- **Represents:** hotkey (`rightCtrl`|`rightAlt`), sounds, showTextInBar, modelFile, serverURL, mode,
  language, onboarded, showBar, liveTranscription — the Mac `Preferences.Key` set with Windows values.
- **Owned by:** the Windows user.
- **States:** absent → defaults; written atomically (temp file + replace) on every change.
- **Reversible:** yes. **Never silently deleted:** by update or uninstall (R5).

### Device token
- **Represents:** the bearer token for one server URL. **Owned by:** the Windows user.
- **States:** absent → saved (Save) → deleted (Sign out). Only those two buttons write it.
- **Never silently deleted:** a 401 shows "Token invalid" and keeps the token.

### Speech model file
- **States:** absent → `.partial` (downloading) → verified final file → deleted (Delete button).
- **Never silently deleted:** a failed hash deletes only the `.partial`.

### Outbox entry
- **Represents:** an unsent dictation report. In memory only, 200 max, oldest dropped; lost on quit
  (same as the Mac).

### Insights cache (`insights-cache.json`)
- **Represents:** the last successful `/v1/insights` response (numbers only). Replaced on success.

### Release artefacts
- `Spit.dmg`, `Spit-Setup.exe`, `SHA256SUMS.txt`. CI artifacts are disposable; releases are Miguel's.

## 8. Users, auth and permissions

Single role per client: whoever holds a device token. The server enforces auth (bearer tokens, 120
req/min per token, 30 failed auths/min per IP) and nothing changes there (R21). The Windows client
stores one token per server URL and sends it as `Authorization: Bearer`. Tokens are issued by Miguel
per device (R20); there is no sign-up UI.

## 9. Technical direction

- **Mac:** existing Swift/SwiftUI app, XcodeGen 2.46, `xcodebuild`, `hdiutil`, `codesign`, `gh`.
- **Windows language / runtime:** C# on .NET 10 (SDK 10.0.401), `Nullable` enabled, warnings as errors.
- **Windows framework:** WPF (`net10.0-windows`, `win-x64`, self-contained publish).
- **Packages (exact):** Whisper.net, Whisper.net.Runtime, Whisper.net.Runtime.NoAvx,
  Whisper.net.Runtime.Vulkan 1.9.1; NAudio 3.1.0; H.NotifyIcon.Wpf 2.4.1; Velopack 1.2.0 (vpk tool
  1.2.0); Meziantou.Framework.Win32.CredentialManager 3.0.4. Tests: xunit.v3 4.0.1,
  xunit.runner.visualstudio 4.0.0, Microsoft.NET.Test.Sdk 18.10.0. Win32 interop by hand-written
  `[DllImport]` / `[LibraryImport]` in `Platform/Native.cs` — no CsWin32.
- **Database:** none on the clients; JSON files as in §7. Server unchanged (Fastify + SQLite).
- **Rendering:** native desktop UI; `/spit` is static HTML reusing `site/styles.css`.
- **Background work:** hook thread (own message loop); capture on NAudio's thread; transcription and
  HTTP on the thread pool; UI updates marshalled to the WPF dispatcher.
- **Hosting / deploy target:** GitHub Actions `windows-latest` builds the exe; GitHub Releases hosts
  assets (Miguel publishes). The site host is open question 5.

### External integrations

| Integration | Used for | Timeout | Local fake |
|---|---|---|---|
| Spit server `https://voice.miraside.co` | refine, dictations, me, settings, insights, dictionary, health | 10 s; `/v1/refine` = `Budget.ms`; `HEAD /health` 3 s | `StubApi` in `Spit.Core.Tests` (port of `StubAPI.swift`) |
| Hugging Face model download | `ggml-large-v3-turbo(-q5_0).bin` | 30 s connect, no total cap; resumable restart from zero | tests hash a small local file against a test-only pin |
| Whisper.net / whisper.cpp | speech to text | none (bounded by audio length) | `ITranscriber` + `FixedTextTranscriber` (port of the Mac's) |
| WASAPI via NAudio | microphone | start 3 s | `IAudioSource` fed from a WAV file in smoke-test mode |
| Win32 keyboard hook | hotkey | callback does no work | `RawKeyEvent` sequences in tests |
| Win32 clipboard + `SendInput` | paste | open 200 ms, restore 1.5 s | `Spit.App.Tests` round-trip on the CI runner |
| Credential Manager | token | n/a | test target `co.miraside.voice.test:<guid>` deleted after the test |
| HKCU Run key | launch at login | n/a | test value name `SpitTest-<guid>` deleted after the test |
| GitHub Releases / Actions | shipping | n/a | workflow artifacts on PRs |

## 10. Security and privacy

- **Sensitive data:** the device token (Credential Manager / Keychain only); dictated audio (memory
  only); dictated text (memory, the clipboard for ≤ 1.5 s with history exclusion, and the server).
- **Never leaves the machine:** audio. Never sent anywhere: window titles, file paths, the user's
  clipboard snapshot.
- **Retention and deletion:** local files survive uninstall by design (R44); the page will say how to
  remove `%LOCALAPPDATA%\Miraside\Spit\` and the credential.
- **Riskiest surfaces:** the model download (guard: pinned SHA-256, `.partial` rename); the keyboard
  hook (guard: listen-only, no logging of key codes outside `--key-log`, which prints to the console
  only and is never on by default); the clipboard (guard: snapshot formats and size limits, owner HWND);
  CI (guard: `GITHUB_TOKEN` only, `permissions: contents: read` except the tag workflow's `write`).

## 11. Build order

1. Build spec and task list committed on `spit-mac-windows`; pushed.
2. `VERSION` + Mac DMG + `verify-dmg.sh` passing locally (task 2.0). Commit.
3. `windows/` scaffold: solution, props, three projects, shared contracts (`Dictation` types,
   `IVoiceApiClient` + DTOs, `Insights` DTOs, `ITranscriber`, `IRefiner`). `dotnet build` passes.
   Commit and push so CI can start.
4. `Spit.Core` logic and tests in three disjoint slices (App+Hotkey+Audio+Inject; Asr+Refine;
   Insights), then `parity-check.sh`. Commit.
5. CI workflow running Core tests on Ubuntu and a Windows build of `Spit.App`. Push; green.
6. `Spit.App` platform layer, then Coordinator, then UI, then smoke-test mode. Each compiles on the Mac.
7. `Spit.App.Tests`, pack script, CI smoke + install jobs. Push; green, `Spit-Setup.exe` artifact.
8. Release workflow, docs, `/spit` page. Final full verification loop (§13). Open the PR.

## 12. Testing

- **Unit:** all 108 ported tests; `HotkeyTranslatorTests` (R28), R46 sync tests, R43 time-zone test,
  model hash verification, `HttpVoiceApiClient` against a fake `HttpMessageHandler`.
- **Integration (Windows CI):** `Spit.App.Tests` — clipboard snapshot/restore round-trip, elevation
  probe on own process, foreground context of a spawned process, credential round-trip, Run-key
  round-trip.
- **End-to-end:** Journey A in CI as far as a runner allows: silent install, process alive after 15 s,
  second instance exits, `--smoke-test` transcribes `mac/Fixtures/en.wav` with the q5_0 model to
  non-empty text, uninstall leaves the data folder. Journey D locally via `package.sh` +
  `verify-dmg.sh`. Journeys B and C at the PC (manual checklist).
- **Commands:**
  - `export PATH="$HOME/.dotnet:$PATH"; dotnet test windows/Spit.Core.Tests && windows/scripts/parity-check.sh`
  - `cd mac && xcodebuild test -project Voice.xcodeproj -scheme Voice -derivedDataPath build/test-dd -quiet`
  - `mac/scripts/package.sh` (runs `verify-dmg.sh`)
  - CI: `.github/workflows/windows-ci.yml`

## 13. Acceptance scenarios — your finish line

### AC-1 — The DMG is correct (Journey D)
- **Given** a clean checkout of `spit-mac-windows` with `VERSION` = `0.2.0`
- **When** `mac/scripts/package.sh` runs
- **Then** it exits 0; `verify-dmg.sh` reports PASS for: `hdiutil verify`; volume name `Spit`;
  `Spit.app` and `Applications -> /Applications` present; `codesign --verify --deep --strict` OK;
  `TeamIdentifier=FZC6P6XRGD`; `lipo -archs` prints exactly `arm64`; `CFBundleShortVersionString` =
  `0.2.0`; `CFBundleIdentifier` = `co.miraside.voice`; and `xcodebuild test` reports 0 failures.

### AC-2 — Release mode refuses ad-hoc (invariant 12)
- **Given** `VOICE_SIGN_IDENTITY` unset and `VOICE_FORCE_NO_IDENTITY=1` (test hook that hides the
  identity lookup)
- **When** `mac/scripts/package.sh --release` runs
- **Then** it exits non-zero with "refusing to build a release without an Apple Development identity"
  and no `build/Spit.dmg` is written and nothing is uploaded.

### AC-3 — Core parity
- **Given** the Mac tests in `mac/VoiceTests`
- **When** `dotnet test windows/Spit.Core.Tests` and `windows/scripts/parity-check.sh` run on the Mac
- **Then** 0 failures, and every one of the 14 classes has the same test count and names as its Swift
  file (108 total), plus the added R28/R43/R46 tests pass.

### AC-4 — The hotkey is not cancelled by AltGr, repeats or Spit's own paste (invariant 3)
- **Given** the translator + interpreter holding a Right Ctrl dictation
- **When** it receives a repeated Right Ctrl key-down, a key-down with scan code `0x21D`, and an
  injected `V` key-down
- **Then** no `.cancel` is emitted and the release still yields `.release`.

### AC-5 — The PC never resets the Mac's hotkey (invariant 6)
- **Given** a `StubApi` whose `/v1/me` returns `hotkey: "rightCommand"`, `llmModel: "gpt-oss:120b"`
- **When** sync succeeds and Mode changes to `literal`
- **Then** exactly one PUT is recorded with `hotkey: "rightCommand"`, `llmModel: "gpt-oss:120b"`,
  `mode: "literal"`; and with `/v1/me` failing, a Mode change records zero PUTs.

### AC-6 — The exe builds, installs, runs and transcribes on Windows (Journey A)
- **Given** a push to `spit-mac-windows`
- **When** `windows-ci.yml` runs
- **Then** every job is green: Core tests (Ubuntu and Windows), `Spit.App.Tests`, pack produces
  `Spit-Setup.exe` (uploaded as an artifact), silent install creates `%LocalAppData%\Spit\Spit.exe`,
  the app is alive after 15 s, a second launch exits 0 within 10 s, `--smoke-test` on `en.wav`
  produces non-empty text containing at least one word of the fixture's known transcript, and uninstall
  removes `%LocalAppData%\Spit\` but not `%LOCALAPPDATA%\Miraside\Spit\`.

### AC-7 — A tampered model is never loaded (invariant 8)
- **Given** a `.partial` whose SHA-256 differs from the pin
- **When** `ModelManager` finishes the download
- **Then** it deletes the `.partial`, reports "Model download failed — checksum mismatch", and no final
  file exists.

## 14. Failure recovery

- **Errors surface in:** the bar (one line, user copy from `Strings`) and a rolling log in
  `%LOCALAPPDATA%\Miraside\Spit\logs\spit-YYYYMMDD.log` (lengths, timings, error types, HTTP status —
  never text). Mac: unchanged `Logger`.
- **Background retries:** outbox flush on the next successful request (Mac behaviour); sync every
  600 s; model download is retried only by the user pressing Download again; keyboard hook re-installed
  on resume, unlock and every 5 min idle.
- **Migrations / rollback:** none; `settings.json` unknown keys ignored, missing keys defaulted.
- **Never do this:** restore the clipboard before the target read it, or swallow a paste failure
  silently — both lose the user's words.

## 15. Deliverables

- [ ] `mac/scripts/package.sh` + `verify-dmg.sh`, `VERSION`, `project.yml` changes; a locally verified
      `mac/build/Spit.dmg` (not committed)
- [ ] `windows/` solution: `Spit.Core`, `Spit.Core.Tests`, `Spit.App`, `Spit.App.Tests`, scripts
- [ ] `.github/workflows/windows-ci.yml` (green on the PR) and `release-windows.yml`
- [ ] `docs/API.md` note; `README.md` Windows section and counts
- [ ] `../../site/spit/index.html` (outside git)
- [ ] Task list ticked; Decisions Log filled; a PR from `spit-mac-windows` to `main` whose description
      lists every (PC) item still open

## 16. Decisions log

Append one line per decision you made that this spec did not settle, in the form:
`<what you decided> — <why, in one clause>`. Also record anything you deliberately did not
build because §2 excluded it.

- Open questions defaulted for the build: Q1 `/spit` not linked from `index.html`; Q2 accept the
  Smart App Control block (no signing); Q3 no threshold enforced, CI records CPU ms; Q4 keep
  `rightCtrl`+`rightAlt`; Q5 page written to `site/spit/index.html`, not deployed — each is Miguel's to
  reverse.
- Verification of the `.exe` happens on GitHub's `windows-latest` runners, plus a `--smoke-test` mode and
  a Windows-only test project — the only way to run Windows code without Miguel's PC.
- `mac/project.yml` keeps a copy of the version (`MARKETING_VERSION`) and `Info.plist` reads
  `$(MARKETING_VERSION)` — XcodeGen cannot read a file; `scripts/check-version.sh` fails `package.sh` and
  CI when the copy drifts from `VERSION`.
- `package.sh` builds tests into `build/test-dd` and never touches `mac/build/Spit.app` — Miguel's own build
  lives there.
- `--release` also refuses when the GitHub release is missing or already published — rule "never replace a
  published asset" made mechanical.
- Velopack 1.2.0 names the installer `Spit-win-Setup.exe` (channel `win`), not `Spit-Setup.exe` as PRD rule 8
  assumed — verified by cross-packing on the Mac; `pack.ps1` renames it so the release asset name holds.
- xunit.v3 runs on Microsoft.Testing.Platform via root `global.json` (`test.runner`) — the .NET 10 SDK refuses
  VSTest for it; `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are therefore not referenced.
- Every `Spit.Core` type lives in the single namespace `Spit.Core` — a namespace named `Insights` beside a
  type named `Insights` makes every reference ambiguous in C#.
- While the hotkey is held, modifier key-downs (Shift, Ctrl, Alt, Win, Caps Lock) are not "another key" — the
  Mac sees modifiers as `flagsChanged`, which never cancels; the key that completes a chord still cancels.
- `DictationMachine`, `TapLatch`, `HotkeyInterpreter` are sealed classes (not structs) — mutable structs copy
  silently in C#; `DictationMachine` takes a `TimeProvider` for `StartedAt`; the Mac's nested `Phase` is
  `DictationPhase`.
- `HotkeyChoice.FromRawValue` returns null for the Mac's values (`fn`, `rightOption`, `rightCommand`), which is
  what keeps the server's hotkey from ever being applied on Windows (rule 46).
- `PasteRouting.OwnBundleId` = `spit.exe` (case-insensitive); the Mac's secure-input tests became elevated-target
  tests under their original names.
- `StreamingPolicy` also ports WhisperKit's `isVoiceDetected` (strictly above 0.3), confirmed-segment accumulation
  and covered-time tracking — needed to reproduce the stream loop without WhisperKit.
- `InsightsTotals.AudioMs` is `long` — Swift's `Int` is 64-bit; an `int` fails to decode past ~596 h of audio.
- `SyncService` has no hotkey callback and never writes the local hotkey; `SyncServiceTests.testServerHotkeyChangeGoesThroughCallback`
  keeps its Mac name and asserts the Windows rule instead (rule 46). `SignOut` clears state only; the Server page
  deletes the credential (rule 44).
- An id counts as IANA when .NET's `HasIanaId` says so, not when it contains '/' — macOS reports `Portugal`, a valid
  IANA name with no slash; an unconvertible zone yields `InsightsNotice.TimeZoneUnknown` and no request.
- Transport errors: DNS / connection refused / connection lost → Offline; TLS/protocol → Server(-1); the per-request
  timeout → Timeout. An empty token sends no `authorization` header. `tz` is fully URL-encoded (the Mac leaves `+`,
  which the server reads as a space).
- `SkipGate` normalises to NFC and counts grapheme clusters, as Swift's `String.count` does; `Budget` gets the same
  count.
- `InsightsCache` writes atomically and rejects JSON with missing keys; `InsightsFormat.RelativeTime` is hand-written
  English ("N minutes ago") since .NET has no relative date formatter.
- `Spit.Core` has no logger; the Mac's debug/info lines are dropped there, so nothing in Core can log text.
- `TextInjector.InsertAsync` returns once Ctrl+V is sent; the restore runs on its own 1.5 s later. Paste, copy and
  restore all decide while holding the clipboard open, so a pending restore never overwrites newer text; a second
  paste during a pending restore keeps the original snapshot. A failed `SendInput` leaves the text on the clipboard
  with no restore (`ClipboardOnlyKeystrokeFailed`) — restoring would erase the dictation.
- Restored clipboard content also carries the history-exclusion format, so the user's old copy is not re-added to
  Clipboard History.
- Elevation: only access-denied counts as elevated; any other failure (e.g. the process exited) is logged and
  treated as not elevated.
- The settings key stays `showTextInHUD` (Mac name) rather than §7's "showTextInBar".
- `WhisperTranscriber` builds a processor per call (language and prompt are fixed at build time), caps the prompt at
  100 words (~150 tokens), and drops whisper.cpp silence tags such as `[BLANK_AUDIO]` so they read as "Nothing heard"
  instead of being pasted.
- `Spit.App.Tests` skips per test with `[WindowsFact]` (xunit v3 has no class-level skip) and sets
  `ValidateExecutableReferencesMatchSelfContained=false` to reference the self-contained app.
- **Every clipboard operation in the app runs on the thread that owns Spit's clipboard owner window (the WPF
  dispatcher thread), and that thread never blocks for long.** When another thread or app calls `EmptyClipboard`
  while Spit owns the clipboard, Windows *sends* `WM_DESTROYCLIPBOARD` to Spit's window and waits for its thread;
  CI caught two clipboard tests deadlocking this way for 3 s. Clipboard tests run in one serial collection.
- Capture uses NAudio 3.1's `WasapiRecorder` (`WasapiCapture` is `[Obsolete]`) on the Console-role default device,
  and `WdlResampler` directly in input-driven mode — `WdlResamplingSampleProvider` zero-pads short reads, and every
  live packet is one. The 20 s warm keep closes the device afterwards (WASAPI cannot re-initialise a stopped
  client); `Start()` waits up to 3 s for capture to really run, so failure throws instead of "Listening" over silence.
- `StreamingSession` computes voice energy from the samples in 100 ms frames, and resumes decoding by slicing audio at
  the last confirmed segment end (WhisperKit takes a seek offset; whisper.cpp does not). Each poll copies the buffer,
  as the Mac does — watch GC pressure on long dictations.
- `MenuMask` injects `vkE8` from the dispatcher, not the hook callback; a Right Alt tap shorter than one dispatcher turn
  could still reach a menu — spike S4 checks it.
- Tray icons are drawn shapes packed into a multi-size .ico (Segoe MDL2 has no filled or crossed mic); the tray is
  created with Efficiency Mode off so Windows never throttles the hook and capture.
- Views update from `PropertyChanged` in code-behind rather than XAML bindings — a misspelled binding fails silently at
  runtime on Windows, a misspelled property fails the build on the Mac.
- The bar is positioned in physical pixels with `SetWindowPos` and follows foreground changes through a WinEvent hook
  (the Windows counterpart of the Mac's app-activation observer); live text shows beside the waveform while listening.
- Theme follows the registry's light/dark setting, system colours in high contrast, and the user's accent; font is
  "Segoe UI" (WPF cannot select weights of the variable Segoe UI Variable).
- `SingleInstance` calls `AllowSetForegroundWindow` before signalling, so the first instance may really come to front.
- One `MicrophoneState` enum (with `Unknown`) serves capture and the Set-up window.
- Installer size is ~126 MB (self-contained .NET + three Whisper runtimes) — accepted; framework-dependent
  would require friends to install .NET themselves.

## 17. Definition of Done

> AC-1 to AC-7 pass — AC-1/AC-2 via `mac/scripts/package.sh` on this Mac, AC-3 to AC-5 and AC-7 via
> `dotnet test` + `parity-check.sh`, AC-6 as a green `windows-ci.yml` run on the PR's head commit —
> and the PR to `main` is open with every (PC) item listed as outstanding.

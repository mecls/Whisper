# Miraside Voice — Insights Dashboard — Build Spec

**One-line mission:** Give Voice a face — an Insights window showing four cards (total words, words per minute, day streak with heatmap, per-app breakdown), fed by one new server endpoint, turning a menu-bar background service into a real Mac app.

---

## 0. How to use this document

You are building this unattended. Nobody will answer questions while you work, so:

1. **This document outranks your instincts.** Where it names a rule, a number, or a stack choice, follow it even if you would have chosen differently.
2. **When you hit something genuinely unspecified, decide and keep moving.** Do not stall and do not invent scope. Pick the smallest choice consistent with §1 and §2, append it to the Decisions Log in §16 with one line of reasoning, and continue.
3. **§13 is your finish line.** Check your work against those scenarios rather than waiting for a human to confirm. Work is done when they pass, not when the code looks finished.
4. **The non-goals in §2 are binding.** If a change would be genuinely useful but sits outside them, note it in §16 as a suggestion and do not build it.

Two environment facts that will otherwise waste your time:

- **The production server is unreachable.** `https://voice.miraside.co` serves an untrusted certificate (Let's Encrypt never issued; Traefik is serving `CN=TRAEFIK DEFAULT CERT`) and holds **zero dictations**. Do not try to fix it, do not deploy, do not SSH anywhere. Build and test against a local server and an in-memory or temp-file SQLite database. If something you write cannot be verified without the VPS, it belongs in §15's handoff checklist, not in your definition of done.
- **`GO_LIVE.md` and `deploy/VPS.md` are in `.gitignore` on purpose** — they carry host and SSH details and the GitHub repo is public. You will edit `GO_LIVE.md` (§15) and it will correctly not appear in `git status`. Never `git add -f` it.

The source of truth for *why* each rule exists is `tasks/prd-insights-dashboard.md`. This document is the executable form of it; where they disagree, this one wins, and note the disagreement in §16.

---

## 1. Primary user and outcome

**Primary user:** Miguel — one person who dictates into his own Mac all day with Miraside Voice and currently has no way to see anything the app has done for him. A handful of teammates use the same server with their own device tokens, but the window is designed for one person looking at their own numbers.

**Outcome:** Open a window and see, in under a second, how much you have dictated, how fast you speak, whether you have a streak going, and which apps you actually dictate into.

**Narrowing principle:** **Four cards, numbers only, no transcript text anywhere.** Every feature request that arrives during this build — history, search, re-paste, sharing, comparisons — fails this test. The reference product (Wispr Flow) has a dozen cards and two tabs; you are building four cards and one tab.

## 2. Non-goals

- **Percentile or "top X%" comparisons** — they require a population of users to rank against. There are a handful of teammates; any percentile would be fabricated.
- **Sharing or export** — no share button, no image generation, no CSV.
- **A "Your voice" tab** — the reference has a second tab. There is one tab here and it has no tab bar.
- **Transcript history, search, or re-paste** — this is the one most likely to feel obviously useful mid-build. It is excluded because it would put transcripts on screen and on disk, which §10 forbids.
- **A "fixes made" card** — needs a `raw` vs `cleaned` diff, and cleanup is currently blocked behind the unissued certificate, so the card would render zero for everyone.
- **Mobile/desktop split** — there is no mobile client.
- **Any change to the VPS, deploy, or the TLS certificate.**

## 3. Journeys

### Journey A — First look (happy path)
1. User launches Voice. It now appears in the dock and the Insights window opens.
2. Cards render immediately from `~/Library/Application Support/Voice/insights-cache.json` if it exists, otherwise in their loading state.
3. `GET /v1/insights?tz=<current IANA tz>&weeks=21` returns; cards repaint with real numbers; the cache is replaced.
4. User reads four cards: total words, WPM, streak + heatmap, per-app breakdown.

### Journey B — Dictating while Insights is focused (unhappy path)
1. User has the Insights window frontmost and holds the hotkey.
2. Recording, transcription and cleanup proceed exactly as normal.
3. At insert time the dictation's captured frontmost app is Voice itself, so the text goes to the **clipboard instead of being pasted**, and the HUD shows the existing clipboard notice (`Strings.secureField`).
4. Nothing is typed into the Insights window. ⌘V into any other app produces the text.

### Journey C — Offline / expired token (unhappy path)
1. User opens Insights with Wi-Fi off, or with a revoked token.
2. Cached numbers render immediately, unchanged.
3. The refresh fails. A staleness line appears under the header saying when the numbers were last updated. On `401`, the line instead says the token is invalid (`Strings.tokenInvalid`) and links to Settings › Server.
4. No dialog, no alert, no empty window, no retry loop.

### Journey D — Closing the window (happy path)
1. User closes the Insights window. The app does **not** quit.
2. The menu bar icon still works; the hotkey still dictates; the Whisper model stays loaded.
3. Clicking the dock icon reopens the Insights window with the cached numbers.

## 4. Screens and states

### Insights window (`Window(id: "insights")`)
- **Primary action:** read the four cards. There is nothing to click.
- **Secondary actions:** a refresh affordance (⌘R and a menu item), and a link to Settings › Server shown only in the unauthorized state.
- **Layout:** a 2×2 card grid. Default 900×620, resizable, `minWidth: 720`, `minHeight: 520`.
- **Empty** (zero dictations — the state that ships, since the server has none): every card renders its frame with `—` in place of its number and one line of copy: "Dictations will appear here once you start using Voice." Never a spinner that never resolves.
- **Loading** (no cache and a request in flight): card frames with a shimmer or dimmed placeholder in the number's position. Loading is only ever visible on genuine first run; with a cache present, rule "render cache first" means the user never sees it.
- **Error** (request failed, cache present): the cards keep showing cached numbers, plus a single line under the window header: "Last updated <relative time> · couldn't reach the server". On 401: "Token invalid — open Settings › Server".
- **Error** (request failed, no cache): the empty state plus the same staleness line. Never a dialog.
- **Success:** four populated cards.

### The four cards
| Card | Populated | Empty |
|---|---|---|
| **Total words** | Large number, thousands-separated, e.g. `64,860`, caption "total words" | `—` |
| **Words per minute** | Large number, caption "words per minute" | `—` when below 60 000 ms total audio (§6 rule 8) |
| **Streak** | "`18` day streak" plus the heatmap grid; a caption showing the longest streak | `0 day streak` and an all-empty grid |
| **Apps** | Up to 7 rows (6 apps + `Other`), each a label, a bar and a percentage | `—` and the empty copy |

**Heatmap at narrow widths** (resolves PRD §7 Q3): the grid keeps a fixed cell size and **scrolls horizontally inside its card**, newest week pinned right and visible on open. Do not drop weeks and do not shrink cells below 10 pt — a heatmap that silently shows fewer weeks at one window size is a lying chart.

## 5. Capabilities

- [ ] `GET /v1/insights` returning totals, streak, per-day buckets and per-app breakdown
- [ ] `dictations.word_count` column, written by the existing upsert
- [ ] `backfillWordCounts` for NULL rows, run once at boot
- [ ] `npm run seed` CLI generating synthetic dictation history
- [ ] Insights window with four cards and all four states
- [ ] Contribution heatmap (plain SwiftUI, no charting dependency)
- [ ] Disk cache of the last successful response
- [ ] Voice promoted to a regular dock app
- [ ] Clipboard fallback when Voice is the frontmost app at insert time
- [ ] Menu bar item opening Insights

## 6. Invariants — enforce on the server

These are the numbered rules from `tasks/prd-insights-dashboard.md` §2, restated as things you must not violate. The PRD carries the full reasoning for each.

1. **`GET /v1/insights` returns numbers only — never `raw`, never `cleaned`, never a preview or a sample.** This is the invariant the whole feature rests on and §13 AC-4 asserts it against the serialized response.
2. **The insights handler never `SELECT`s `raw` or `cleaned`.** It reads `word_count`, `audio_ms`, `created_at`, `app_name`, `app_bundle_id` and nothing else, so it cannot leak text it never loaded. No `req.log` line may carry transcript text; follow the refine route, which logs `rawChars` (a length).
3. **`tz` is validated against `Intl.supportedValuesOf('timeZone')`; an unknown zone is a `400`, never a silent fallback to UTC.** A typo that quietly buckets days in UTC produces a plausible wrong streak that nobody would catch.
4. **Day bucketing happens in Node with `Intl.DateTimeFormat(tz, …)`, not in SQLite.** SQLite has no timezone database and a fixed UTC offset is wrong on the far side of every DST change. Node has full ICU (418 zones, verified on this machine).
5. **One definition of a word: `countWords(text)` = whitespace-separated tokens after trimming.** Called in exactly two places — the write path and the backfill — and never by the insights handler.
6. **`word_count` is maintained by the existing single-statement upsert. No read-back, no second write.** JS computes `countWords(cleaned ?? raw)` from the payload and passes it as a column; the conflict clause is:
   ```sql
   word_count = CASE WHEN excluded.cleaned IS NOT NULL
                     THEN excluded.word_count
                     ELSE dictations.word_count END
   ```
   This is exact because `raw` is frozen at first insert (the upsert contract in `docs/API.md` never updates it) and `cleaned` resolves to `coalesce(excluded.cleaned, dictations.cleaned)` — so the only way a row's text changes is a new non-null `cleaned`, which is the branch JS has the text for. **The `ELSE` branch is load-bearing:** without it, an outbox replay carrying `cleaned: null` for a row the server had already cleaned would silently downgrade its count to the raw text's.
7. **A NULL `word_count` is excluded from word totals, never summed as zero**, and is backfilled at boot. The column is nullable on purpose; `NOT NULL DEFAULT 0` would make pre-existing rows claim zero words forever.
8. **WPM = `totalWords / (totalAudioMs / 60000)`, rounded, over rows with `audio_ms > 0`; `null` below 60 000 ms of total audio.** It is speaking rate, not typing rate. A single three-second dictation yields ~340 wpm, which is true and meaningless.
9. **Streak = consecutive local calendar days with ≥1 dictation, counting back from today in `tz`; if today has none, start at yesterday; 0 if neither.** Starting at yesterday matters — otherwise the number reads 0 every morning until the first dictation.
10. **The streak is computed over all history; only the heatmap is windowed by `weeks`.** A 60-day streak viewed through a 21-week window must still read 60.
11. **Days with no dictations appear in the response with zeroes, never omitted.** The client draws a fixed grid; making it infer gaps is how off-by-one weeks happen.
12. **App breakdown returns the top 6 by dictation count plus `Other`.** Label is `app_name` → `app_bundle_id` → `Unknown`; all three are nullable. Shares round so they sum to exactly 100.
13. **A user only ever sees their own dictations.** Every query filters on `req.user.id`. There is no admin view and no cross-user aggregate.

### Client-side invariants (not server-enforced, but non-negotiable)

14. **A dictation must never paste into Voice itself.** At insert time, if the dictation's captured frontmost app has bundle id `co.miraside.voice`, route to the clipboard and report `Injected.clipboard`, reusing the existing `InsertResult.clipboardOnly` handling and `Strings.secureField` copy. Do not invent a second mechanism.
15. **The HUD stays a non-activating `NSPanel`.** The app becoming `.regular` must not let the HUD take focus; stealing the caret mid-dictation is the failure the whole injection design exists to avoid.
16. **`LSUIElement` and the activation policy change together, in the same commit.** `LSUIElement` is read from Info.plist before any of your code runs.
17. **No transcript text is written to disk on the Mac.** The cache file holds the response verbatim, which by invariant 1 contains no text. The app's stated privacy property is that transcripts never land on disk.

## 7. Data model and lifecycle

### `dictations` (existing table — `server/src/db/schema.ts`)
- **Represents:** one dictation. Already carries `created_at`, `raw`, `cleaned`, `audio_ms`, `asr_ms`, `llm_ms`, `total_ms`, `app_name`, `app_bundle_id`, `language_detected`, `injected`, `mode`, `fallback_reason`, `llm_model`, `client_version`.
- **Owned by:** `user_id`, unique on `(user_id, client_id)`.
- **Change:** add `word_count integer` (nullable) via `server/src/db/migrations/0003_word_count.sql`. Migrations are hand-written SQL applied in filename order by `applyMigrations` — follow `0002_total_ms.sql` exactly as a model.
- **States:** a row is created by `/v1/refine` or `POST /v1/dictations` and updated by replays and by `PATCH …/by-client/:clientId`. `raw`, `mode`, `created_at`, `asr_model` and `client_version` are frozen at first insert; `cleaned`, `injected`, `llm_ms`, `total_ms`, `llm_model`, `language_detected` and now `word_count` can be updated.
- **Permanent:** nothing is ever deleted. There is no delete path and you must not add one.

### `insights-cache.json` (new, client)
- **Represents:** the last successful `GET /v1/insights` response, verbatim.
- **Owned by:** the Mac, at `~/Library/Application Support/Voice/insights-cache.json`.
- **Lifecycle:** written on every successful refresh, read on every window open. Reversible and disposable — deleting it costs one refresh. A corrupt or unparseable file is treated as absent, not as an error.

## 8. Users, auth and permissions

**Multi-user server, single-user view, no roles.** Every request carries a bearer device token (`mv_…`) resolving to one `user_id`; `GET /v1/insights` filters every query on `req.user.id`. There is no admin, no team view, no way for one user to see another's numbers, and you must not add one. Enforce the boundary in the query itself, not by hiding UI — §13 AC-3 tests it with two seeded users.

On the Mac the token comes from the Keychain exactly as the existing `VoiceAPI` does it (`Keychain.token(for: Preferences.serverURL)`); reuse that client, do not build a second one.

## 9. Technical direction

This is an existing codebase. Match what is there; do not introduce a new stack.

- **Server:** Node 22, TypeScript (strict, ESM), Fastify with `fastify-type-provider-zod`, Drizzle ORM over `better-sqlite3`. Tests are `node:test` with `node:assert/strict`. Run with `npm test` from `server/`.
- **Mac:** Swift 6.3 / SwiftUI, macOS 26 target, project generated by **XcodeGen** from `mac/project.yml`. Tests are XCTest. **Re-run `xcodegen generate` after adding any file** — sources are globbed from the directory, and a new file is invisible to the build until you do.
- **Database:** SQLite (WAL), hand-written SQL migrations in `server/src/db/migrations/`, applied in filename order.
- **Hosting:** Docker Compose on a VPS behind an existing Traefik. **Out of bounds for this build** — do not deploy, do not SSH, do not touch `deploy/`.
- **No new dependencies.** The heatmap is a SwiftUI `LazyVGrid` of `RoundedRectangle`s. No charting library on either side.

### External integrations
| Integration | Used for | Timeout | Local fake |
|---|---|---|---|
| `GET /v1/insights` (own server) | All dashboard numbers | 10 s, matching `VoiceAPI`'s existing `timeoutIntervalForRequest` | `StubAPI` in `mac/VoiceTests/` — extend it, do not write a second double |
| Keychain | Device token | n/a | Existing tests inject a token provider; follow that |

The live server is unreachable (§0). Run the server locally (`cd server && npm run dev`) or drive it in-process with `buildApp({ db: openDb(':memory:'), … })` the way `server/test/routes-refine.test.ts` does.

## 10. Security and privacy

- **Sensitive data:** dictation transcripts (`raw`, `cleaned`) and the device token. Transcripts live in the server's SQLite only. The token lives in the login Keychain.
- **Never leaves the server:** transcript text. Invariants 1 and 2 make this structural rather than a matter of care — the handler does not load the columns, so it cannot serialize them.
- **Never written to the Mac's disk:** transcript text, including in the new cache file (invariant 17) and including in logs.
- **Retention:** unchanged. Dictations are kept indefinitely; this feature adds no deletion path and no export path.
- **Riskiest surface here:** the new endpoint is the first one that returns an aggregate over a user's whole history. The guardrail is invariant 13 (every query filtered on `req.user.id`) plus AC-3, which seeds two users and asserts isolation.
- **Input validation:** `tz` and `weeks` are validated by zod with the same strictness as the existing routes; `weeks` is bounded 1–53 so a caller cannot request an unbounded scan.

## 11. Build order

Build in this order. Each step leaves the repo green — run the relevant suite before moving on.

1. **Migration + column.** `0003_word_count.sql`, `word_count` in the Drizzle schema, `totalMs`-style comment. `npm test` still passes (the migration-count test is already written to survive new migrations).
2. **`countWords` + the upsert `CASE`** in `dictations-repo.ts`, plus `backfillWordCounts`, wired into boot in `app.ts`. Tests for both directions of invariant 6 — this is the subtlest code in the build.
3. **`npm run seed`.** You need data before you can meaningfully test the endpoint, and so does the human reviewing your work.
4. **`GET /v1/insights`** — `src/routes/insights.ts`, registered in `app.ts`. All of §13's server scenarios pass at the end of this step.
5. **`docs/API.md`** — the new route and `word_count`. Do it now, while the shape is fresh.
6. **Mac: the dock promotion** — `project.yml`, `AppDelegate`, invariant 14's clipboard routing. The app still builds and the existing 54 tests still pass.
7. **Mac: the client** — `InsightsClient` on the existing `VoiceAPI`, `InsightsCache`, the response types.
8. **Mac: the window** — `InsightsView` and the four cards, all four states, the menu bar item.
9. **`GO_LIVE.md` §3** — append the human-only checks from §15.

## 12. Testing

- **Unit / integration (server):** `cd server && npm test`. Must go from 53 passing to 53 + your new tests, 0 failures. Cover every invariant in §6 that has a number in it. **The test script is `tsx --test test/*.test.ts` — a new test file must be directly in `server/test/` and end in `.test.ts`, or it will silently never run.**
- **Unit (Mac):** `cd mac && xcodegen generate && xcodebuild test -project Voice.xcodeproj -scheme Voice -destination 'platform=macOS,arch=arm64'`. Must go from 54 passing (1 skipped — the opt-in ASR test, leave it skipped) to 54 + yours, 0 failures.
- **Pure functions get tests; SwiftUI views do not.** Put the streak, heatmap-bucketing and state-selection logic in plain testable types, not inside `View` bodies.
- **No end-to-end test.** Everything end-to-end here needs a microphone, a TCC grant and a reachable server; those are in §15's handoff checklist instead.

**One command to check everything you can check:**
```bash
cd server && npm test && cd ../mac && xcodegen generate && xcodebuild test -project Voice.xcodeproj -scheme Voice -destination 'platform=macOS,arch=arm64' -quiet && echo ALL GREEN
```

## 13. Acceptance scenarios — your finish line

### AC-1 — Populated dashboard (happy path, Journey A)
- **Given** a seeded database with 200 dictations across 20 weeks for user A, including a 5-day run ending today and at least 8 distinct `app_name` values
- **When** `GET /v1/insights?tz=Europe/Lisbon&weeks=21` is called with A's token
- **Then** `200`, `totals.words` equals the sum of `word_count` over A's rows, `totals.wpm` is a whole number, `streak.current` is `5`, `apps` has exactly 7 entries (6 + `Other`) whose `share` values sum to `100`, and `days` is zero-filled with no gaps

### AC-2 — Bad input and missing auth (failure path)
- **Given** a running server
- **When** `GET /v1/insights?tz=Not/AZone` is called with a valid token
- **Then** `400`, and the response body is the standard `{ error, message }` shape
- **And when** the same route is called with no `Authorization` header
- **Then** `401`, and no query against `dictations` is executed

### AC-3 — One user never sees another's numbers (permission path)
- **Given** users A and B, each with 10 seeded dictations and their own token
- **When** `GET /v1/insights` is called with A's token
- **Then** `totals.dictations` is `10`, not `20`, and no day bucket or app row contains any of B's data

### AC-4 — No transcript text escapes (the invariant that matters most)
- **Given** a dictation whose `raw` is the distinctive string `ZZQX-CANARY-TRANSCRIPT-ZZQX` and whose `cleaned` is `ZZQX-CANARY-CLEANED-ZZQX`
- **When** `GET /v1/insights` is called and the response is serialized to a string
- **Then** neither canary appears anywhere in it, and no key named `raw` or `cleaned` exists at any depth

### AC-5 — A replay never downgrades a word count (invariant 6's `ELSE` branch)
- **Given** a dictation stored with `raw` = 10 words and later cleaned to 6 words, so `word_count` is `6`
- **When** `POST /v1/dictations` replays the same `clientId` with `cleaned: null`
- **Then** `word_count` is still `6`, not `10`

### AC-6 — Streak survives a narrow window (invariant 10)
- **Given** a 30-consecutive-day run of dictations ending today
- **When** `GET /v1/insights?weeks=1` is called
- **Then** `streak.current` is `30` while `days` contains only the requested window

### AC-7 — Offline client renders cached numbers (Journey C)
- **Given** a valid `insights-cache.json` on disk and an unreachable server
- **When** the Insights window opens
- **Then** the cached numbers render, a staleness line is shown, and no alert or dialog is presented

## 14. Failure recovery

- **Errors surface in:** the server's existing pino logger (never with transcript text), and on the Mac in the window's staleness line. Nothing is swallowed silently.
- **A failed refresh is not an error state to recover from** — it leaves the cache in place and tries again on the next open. No retry loop, no backoff timer, no background polling.
- **A corrupt `insights-cache.json` is treated as absent.** Parse failure → loading state → refresh. Never crash, never show a parse error to the user.
- **Migrations are forward-only.** `0003_word_count.sql` adds a nullable column; there is no down migration, matching the existing convention.
- **`backfillWordCounts` must be idempotent and non-fatal.** Running it twice changes nothing; if it throws, log and continue booting — the app must still serve dictations with a partially backfilled table.
- **Never do this:** return `0` for a NULL `word_count`. A partial backfill must understate word totals visibly, not invent numbers that look plausible.

## 15. Deliverables

- [ ] Working feature, both halves, on a branch (§16)
- [ ] `server/src/db/migrations/0003_word_count.sql`
- [ ] `npm run seed` — **does not exist yet; add the `"seed"` script to `server/package.json`** alongside the existing `bench` and `migrate` entries, backed by a new `src/cli/seed.ts` (follow `src/cli/users.js`'s shape). It inserts ~200 synthetic dictations for a named user across ~20 weeks, spanning several apps, with a deliberate gap so the streak is not trivially the whole range. Idempotent by user, and it must refuse to run against anything but a local database — guard on `env.databasePath()` not being the deploy path.
- [ ] `docs/API.md` updated with the route and `word_count`
- [ ] Server tests and Mac tests, all passing, counts reported in your final message
- [ ] **Human-only checks appended to `GO_LIVE.md` §3** (the file is gitignored — edit it, do not commit it): dictate with Insights focused and confirm nothing is typed into it; close the window and confirm the app keeps dictating; click the dock icon and confirm the window returns; confirm the three TCC permissions still work after the `.regular` change; confirm the heatmap scrolls rather than dropping weeks at `minWidth`
- [ ] The Decisions Log in §16, filled in
- [ ] A final message stating test counts, what you could not verify, and anything you put in §16

## 16. Decisions log

Work on a branch off `main` named `feat/insights-dashboard`. **Commit as you go** — one commit per step in §11, with messages explaining why, not what. **Do not push and do not open a PR.** End commit messages with:

```
Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

Append one line per decision this spec did not settle, in the form `<what you decided> — <why, in one clause>`. Also record anything you deliberately did not build because §2 excluded it.

Decisions already made for you, so you do not re-litigate them:
- Dock icon persists when no window is open; the app is `.regular` from launch to quit.
- The heatmap scrolls horizontally at narrow widths rather than dropping weeks.
- Cleanup is server-only — do not add an on-device LLM path. See `tasks/prd-sub-second-dictation.md` §1a for the measurements that settled it.

## 17. Definition of Done

> `cd server && npm test` and `cd mac && xcodegen generate && xcodebuild test -project Voice.xcodeproj -scheme Voice -destination 'platform=macOS,arch=arm64'` both report **0 failures** with every scenario in §13 covered by a named test; a Release build of `Voice.app` succeeds; `npm run seed` followed by launching the app against a local server renders all four cards populated; and the human-only checks are written into `GO_LIVE.md` §3.

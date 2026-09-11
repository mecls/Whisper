# Insights Dashboard — Implementation Spec

## 1. Objective

Voice has no face. `LSUIElement: true` (`mac/project.yml:43`) plus `NSApp.setActivationPolicy(.accessory)`
(`VoiceApp.swift`, `applicationDidFinishLaunching`) mean it has no dock icon, never appears in ⌘-Tab,
and owns no window except Settings and the first-run onboarding. It is, accurately, a background
service with a menu bar icon — you hold a key, text appears, and there is nowhere to go to see what
the thing has been doing for you.

This adds an **Insights window** as the app's main window, promotes Voice to a regular dock app, and
adds one endpoint — `GET /v1/insights` — that returns the numbers already aggregated. The window
shows four cards: total words dictated, words per minute, a day streak with a contribution heatmap,
and which apps you dictate into.

The endpoint is the whole design. The alternative considered and rejected was aggregating on the Mac
over `GET /v1/dictations`, which would have meant downloading every transcript you have ever dictated
just to count words. With the aggregation server-side, **no transcript text crosses the wire for this
feature at all** — the response is numbers only, and that is a property the tests can assert rather
than a promise the code has to keep by hand.

A `dictations.word_count` column, written when the row is written, carries that one step further: the
insights query never selects `raw` or `cleaned`, so it cannot leak them even by accident. Doing this
now is close to free — the table currently holds **zero** rows, so the backfill is a no-op — and it
gets materially more expensive once there is history to migrate.

## 2. Business rules (invariants — never violate)

### Becoming a real app

1. **Activation policy and `LSUIElement` change together, or the app half-changes.** Set
   `LSUIElement: false` in `mac/project.yml` **and** `NSApp.setActivationPolicy(.regular)` in
   `applicationDidFinishLaunching`. `LSUIElement` is read from Info.plist at launch, before any of
   your code runs, so changing only the policy leaves a launch flicker and changing only the plist
   leaves the policy line contradicting it. Both, same commit.

2. **A dictation must never paste into Voice itself.** When `FrontmostContext.current()` resolves to
   bundle id `co.miraside.voice`, the dictation is put on the clipboard and the HUD shows the
   clipboard notice — reusing the existing `InsertResult.clipboardOnly` path and `Strings.secureField`
   copy rather than inventing a second mechanism. This rule did not need to exist while the app was
   `.accessory`, because it could not be frontmost; it exists now because it can, and the failure it
   prevents is a ⌘V into whatever control happens to have focus in the Insights window.

3. **The HUD stays non-activating.** `HUDPanel` is a non-activating `NSPanel` and must remain one
   after the policy change. A regular app whose HUD takes focus would steal the caret mid-dictation,
   which is the one thing the whole injection design exists to avoid.

4. **Closing the Insights window must not quit the app.** `applicationShouldTerminateAfterLastWindowClosed`
   returns `false`. The menu bar item, the hotkey and the model stay live with no window open —
   closing a window is not a request to stop dictating.

5. **Clicking the dock icon with no window open reopens Insights.** Implement
   `applicationShouldHandleReopen(_:hasVisibleWindows:)`. A dock icon that does nothing when clicked
   reads as a broken app.

### The endpoint and the stored word count

6. **`GET /v1/insights` returns numbers only. No transcript text, ever.** Not `raw`, not `cleaned`,
   not a preview, not a "longest dictation" sample. This is the invariant that makes the whole
   feature safe, and §5 asserts it directly against the serialized response rather than trusting
   review. Registered in `src/routes/insights.ts`, wired in `app.ts` next to `registerDictations`.

7. **Request:** `tz` (IANA name, e.g. `Europe/Lisbon`) and `weeks` (1–53, default 21). `tz` is
   validated against `Intl.supportedValuesOf('timeZone')`; anything else is a 400 rather than a
   silent fallback, because a typo that quietly buckets a user's days in UTC produces a plausible
   wrong streak and nobody would notice.

8. **Day bucketing happens in Node, not SQLite, using the supplied `tz`.** SQLite has no timezone
   database; a fixed UTC offset would be wrong for every historical day on the other side of a DST
   change. Node ships full ICU (418 zones, verified) — use `Intl.DateTimeFormat` with `en-CA` to get
   `YYYY-MM-DD` directly.

9. **One definition of a word, in one function, used only on the write path.** `countWords(text)` =
   the number of whitespace-separated tokens after trimming. It is called in exactly two places —
   the dictation write path (rule 10) and the backfill (rule 12) — and never by the insights
   handler. Every number on the screen derives from it, so two callers disagreeing about what a word
   is would make the whole window suspect; the way to guarantee they cannot is to have only one.

10. **`word_count` is written by the existing single-statement upsert — no read-back, no second
    write.** The counted text is `cleaned ?? raw`; `cleaned` wins because that is what was actually
    pasted. JS computes `countWords(body.cleaned ?? body.raw)` from the payload alone and passes it
    as a column, and the conflict clause is:

    ```sql
    word_count = CASE WHEN excluded.cleaned IS NOT NULL
                      THEN excluded.word_count
                      ELSE dictations.word_count END
    ```

    This is exactly correct, and the reason is worth writing down because it is not obvious. The
    final text of a row can only change in one way. `raw` is frozen at first insert — the upsert
    contract in `docs/API.md` never updates it — and `cleaned` resolves to
    `coalesce(excluded.cleaned, dictations.cleaned)`. So:

    - **Insert:** JS holds both `cleaned` and `raw` and computes the final count exactly.
    - **Conflict, payload has `cleaned`:** that text becomes the row's text, and JS counted it.
    - **Conflict, payload has no `cleaned`:** the stored `cleaned` survives and `raw` is frozen, so
      the row's text is unchanged — which means the stored count is already right and must be left
      untouched. Overwriting it with the payload's raw-based count here is the bug this clause
      exists to prevent: an outbox replay of a dictation the server had already cleaned would
      silently downgrade its count to the raw text's.

    A dictation that arrives raw from the outbox and is cleaned later by `/v1/refine` therefore ends
    up counted on its cleaned text, with one statement and one definition of a word.

11. **The insights handler never selects `raw` or `cleaned`.** It reads `word_count`, `audio_ms`,
    `created_at`, `app_name` and `app_bundle_id`, and nothing else. This is what makes rule 6
    structural rather than a discipline — the handler cannot leak a transcript it never loaded. No
    `req.log` line may carry transcript text either; the refine route logs `rawChars`, a length, and
    this follows that convention.

12. **A NULL `word_count` is backfilled, never summed as zero.** The migration adds the column
    nullable, because `NOT NULL DEFAULT 0` would make every pre-existing row claim zero words and
    quietly understate every total forever. `backfillWordCounts(db)` runs once at boot after
    migrations, fills NULL rows using rule 9, and is a no-op when there are none — which is the case
    today, with zero rows. Until a row is filled it counts as a dictation but is excluded from word
    totals, so a partial backfill understates words rather than inventing them.

13. **The streak is computed over all history; the heatmap only over `weeks`.** A user with a 60-day
    streak opening a 21-week window must still see `60`. Computing the streak from the windowed day
    list would silently truncate it at the window edge, and the number would be wrong in exactly the
    case the user is proudest of.

### The numbers

14. **Total words** = the sum of `word_count` over every dictation the user owns (rule 9's
    definition, computed at write time).

15. **Words per minute** = `totalWords / (totalAudioMs / 60000)`, rounded to a whole number, summed
    only over dictations with `audio_ms > 0`. It is **speaking** rate, not typing rate — `audio_ms`
    is how long the key was held, which is the only honest denominator.

16. **WPM is `null` below 60 000 ms of total audio**, and the card renders `—`. A single
    three-second dictation yields a number like 340 wpm, which is arithmetically true and completely
    meaningless.

17. **Streak** = the count of consecutive local calendar days, each with ≥1 dictation, counting
    backwards from today in `tz`. If today has none, counting starts at yesterday instead; if neither
    today nor yesterday has one, the streak is 0. Starting at yesterday matters because otherwise
    the number reads 0 every morning until the first dictation, which is both wrong and demoralising.

18. **The heatmap covers `weeks` complete weeks plus the current week**, weekday rows (Sun–Sat) ×
    week columns. Intensity has four fixed levels by dictation count per day: 0, 1–2, 3–5, 6+. Fixed
    thresholds rather than relative-to-max, so a quiet week does not repaint the whole picture.

19. **App breakdown returns the top 6 apps by dictation count plus `Other`.** The label is
    `app_name`, falling back to `app_bundle_id`, falling back to `Unknown` — all three are nullable
    in the schema. Shares are rounded so they sum to exactly 100.

20. **Days with no dictations are present in the response with zeroes**, not omitted. The client
    draws a fixed grid; making it infer gaps from missing keys is how off-by-one weeks happen.

### Client states

21. **Every card renders in four states: loading, empty, error, populated.** Empty is not
    hypothetical — the server currently holds zero dictations, so empty is what the first person to
    open this window will see. Empty shows the card with a dash and one line explaining that
    dictations will appear here, never a spinner that never resolves.

22. **The last successful response is cached to disk and rendered immediately on open.**
    `~/Library/Application Support/Voice/insights-cache.json` — the response verbatim, which by
    rule 6 contains no transcript text. A refresh runs in the background and replaces it.

23. **A failed or unauthorized refresh shows the cached numbers plus a staleness line**, never an
    empty window and never an error dialog. When the token is invalid the window says so and links
    to Settings › Server, reusing `Strings.tokenInvalid`.

## 3. Flows

**Writing a dictation** (changed by rule 10)
`/v1/refine` and `POST /v1/dictations` upsert exactly as they do today — still one statement — with
`word_count` computed in JS from the payload and carried as one more column. A replay that adds
`cleaned` to a row stored raw updates the count through the same clause; a replay that carries no
`cleaned` leaves it alone. There is no read-back, no second write and no separate recount job.

**Opening the window**
1. Render immediately from `insights-cache.json` if present; otherwise the loading state.
2. `GET /v1/insights?tz=<current IANA tz>&weeks=21` in the background.
3. On 200: replace the cache, re-render.
4. On failure: keep what is on screen, add the staleness line (rule 23).

**Timezone changes between opens**
The client sends its *current* timezone every time and the server recomputes. Nothing is cached
server-side against a timezone, so a user who travels sees their days re-bucketed on the next
refresh with no stale state to invalidate.

**First run, server empty**
200 with zeroes and a zero-filled `days` array of the right length (rule 20) → all four cards render
their empty state. This is today's actual state.

**Dictating while Voice is frontmost**
`FrontmostContext.current()` returns Voice's own bundle id → clipboard path, HUD notice (rule 2).
Nothing is typed into the window.

## 4. Surfaces

| Surface | Change |
|---|---|
| **`GET /v1/insights`** (new) | `?tz=&weeks=` → `{ totals: {dictations, words, audioMs, wpm\|null}, streak: {current, longest}, days: [{date, dictations, words}], apps: [{label, dictations, share}], generatedAt }`. Bearer auth like every other route. |
| **`src/routes/insights.ts`** (new) | Handler and the day/streak/app aggregation. |
| **`src/db/migrations/0003_word_count.sql`** (new) | `alter table dictations add column word_count integer;` |
| **`dictations-repo.ts`** | `countWords` (rule 9), one extra column plus one `CASE` in the existing `upsertDictation` statement (rule 10), `backfillWordCounts` (rule 12). |
| **`app.ts`** | `registerInsights` next to `registerDictations`; `backfillWordCounts` after migrations at boot. |
| **Insights window** (new, `Window(id: "insights")`) | The four cards. Default 900×620, resizable. Opens on launch and from the menu bar. |
| **Menu bar** | New item above `Set up permissions…` that opens Insights and activates the app. |
| **Dock** | Voice now has a permanent dock icon (rule 1) and appears in ⌘-Tab. |
| **`AppDelegate`** | `.regular` policy, `applicationShouldTerminateAfterLastWindowClosed → false`, `applicationShouldHandleReopen` (rules 1, 4, 5). |
| **`Strings.swift`** | All new copy — card titles, empty-state lines, staleness line. |
| **`docs/API.md`** | The new route, its query params and response shape; `word_count` in the row column list. |

Cards are plain SwiftUI — the heatmap is a `LazyVGrid` of rounded rects, the WPM card a number with
a caption. No charting dependency is needed at this size.

## 5. Validation

**Server tests** (`node:test`, alongside the existing 53):
- **No transcript text in the response.** Seed a dictation whose `raw` is a distinctive sentence,
  call the route, and assert the serialized JSON does not contain it and that no response key is
  named `raw` or `cleaned`. This is rule 6 and it is the most important test in the feature.
- `countWords`: multiple spaces, newlines, leading/trailing whitespace all collapse; empty → 0.
- **Write path (rule 10), both directions:** a `POST /v1/dictations` with only `raw` stores that
  word count; a later `/v1/refine` for the same `clientId` supplying `cleaned` **updates**
  `word_count` to the cleaned text's count. And the reverse — once a row has been cleaned, an outbox
  replay carrying `cleaned: null` must **leave `word_count` unchanged**, not downgrade it to the raw
  count. The second case is the one a future refactor of the upsert is most likely to break, and it
  fails silently.
- **Backfill (rule 12):** a row with NULL `word_count` is filled at boot; running it twice changes
  nothing; a NULL row is excluded from `totals.words` but still counted in `totals.dictations`.
- WPM: 300 words over 120 000 ms → `150`. Total audio 59 999 ms → `null` (rule 16).
- Streak: today+yesterday+2 days ago → `3`; none today but one yesterday → `1` (rule 17); none in
  either → `0`; two dictations on one day → still `1`; a 30-day run with `weeks=1` → still `30`
  (rule 13).
- Timezone: the same `created_at` bucketing differently under `Europe/Lisbon` vs `Pacific/Kiritimati`,
  and a day spanning a DST change bucketed correctly (rule 8).
- `tz=Not/AZone` → 400 (rule 7).
- Apps: 8 distinct apps → 6 + `Other`; null `app_name` with a present `app_bundle_id` uses the
  bundle id; both null → `Unknown`; shares sum to 100 (rule 19).
- `days` is zero-filled and its length matches the requested window (rule 20).
- A second user's dictations never appear in the first user's totals.

**Manual** (extends the `GO_LIVE.md` §3 checklist):
- Open Insights, dictate into TextEdit, refresh: total words increases by the pasted word count and
  TextEdit appears in the app breakdown.
- **Focus the Insights window and dictate:** nothing is typed into the window, the HUD shows the
  clipboard notice, and ⌘V into TextEdit produces the text (rule 2).
- Close the Insights window: the menu bar icon still works and the hotkey still dictates (rule 4).
  Click the dock icon: the window comes back (rule 5).
- Turn Wi-Fi off and open Insights: cached numbers render with the staleness line, no dialog
  (rule 23).
- Confirm the cache holds no transcript:
  ```bash
  python3 -c "import json;d=json.load(open('$HOME/Library/Application Support/Voice/insights-cache.json'));print(sorted(d))"
  # expect exactly: ['apps', 'days', 'generatedAt', 'streak', 'totals']
  ```

## 6. Out of scope

- **Percentile or "top X%" comparisons** (answer 5A) — they require a population of users to rank
  against; with a handful of teammates any percentile is theatre.
- **Sharing or export** (answer 5B) — the share button in the reference screenshot.
- **A "Your voice" tab** (answer 5C) — the second tab in the reference.
- **Transcript history, search, or re-paste** (answer 4A, rule 6) — the thing most likely to get
  asked for next, and the thing that would put transcripts on screen and on disk.
- **A "fixes made" card** — it needs a `raw` vs `cleaned` diff and is only meaningful once cleanup is
  reliably running; cleanup is server-only by the decision in `prd-sub-second-dictation.md` §7 and is
  currently blocked behind the unissued TLS certificate.
- **Mobile/desktop split** — there is no mobile client.
- **Server-side caching of the aggregate** — recomputed per request. With `word_count` stored it is
  a pure indexed aggregation over one user's rows; if that ever stops being fast enough, the fix is
  a cache, not a second column.

> Note: "no server changes" was originally answer 5E and was **reversed** — an endpoint is now in
> scope, which is what removed the transcript download entirely, and `word_count` followed from it.
> The rest of §6 stands as answered.

## 7. Open questions

1. **Does the dock icon persist when no window is open?** Answer 2C says "full dock app", which I
   have read as yes — `.regular` always, icon always present, rule 5 reopening the window on click.
   The alternative is flipping to `.accessory` when the last window closes, which makes the icon come
   and go and is usually more confusing than helpful. **Decides: Miguel, if my reading is wrong.**
2. **Does promoting to `.regular` affect the TCC grants?** Accessibility and Input Monitoring are
   keyed to the code signature, not the activation policy, so it should not — but the app is ad-hoc
   signed (`GO_LIVE.md` §4), which already re-prompts on every rebuild, so this will be hard to tell
   apart from the existing noise. **Decides: verified by the manual checklist, not by reasoning.**
3. **`weeks` default of 21.** Chosen to match the reference screenshot's roughly five-month heatmap
   at a 900 px window width. If the window is resized much narrower the grid will need to either
   scroll or drop weeks, and that is a layout decision nobody can settle until it is on screen.
   **Decides: whoever builds the window.**

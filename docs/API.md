# Miraside Voice API

Base URL: `https://voice.miraside.co`. All request/response bodies are JSON. Every response echoes the request id on `X-Request-Id`.

## Auth

Every route except `GET /health` requires a bearer device token, minted once by `node dist/cli/users.js add "<name>" --label "<device>"`:

```
Authorization: Bearer mv_<32 random bytes, base64url>
```

Missing, malformed, revoked or unknown tokens, and tokens for a disabled user, get `401` with `{ "error": "missing_token" | "invalid_token" | "token_revoked" | "user_disabled", "message": "Authentication failed" }`. A route-level `404` (e.g. deleting someone else's dictionary entry) uses the same `{ error, message }` shape. Body-validation failures (Zod, via `fastify-type-provider-zod`) return `400` in Fastify's own shape instead: `{ "statusCode": 400, "code": "FST_ERR_VALIDATION", "error": "Bad Request", "message": "body/mode Invalid option: ..." }`.

**Rate limiting returns `429` from two different, unrelated layers with two different bodies — clients must key on the HTTP status code, never on the response shape:**
- The per-token limiter (120 req/min, `@fastify/rate-limit`, keyed by the token's hash — unauthenticated callers share a per-IP bucket): `{ "statusCode": 429, "error": "Too Many Requests", "message": "Rate limit exceeded, retry in ..." }`, and every response carries `x-ratelimit-limit` / `x-ratelimit-remaining` (also present, decrementing, on the successful responses before the 429).
- The per-IP failed-authentication limiter (30 failed authentications/min from one IP; a valid token from that same IP is never blocked by it): `{ "error": "rate_limited", "message": "Too many failed authentications" }` — no `x-ratelimit-*` headers.

**Any unhandled server error** (5xx, including no `statusCode` at all) is normalized to `500 { "error": "server", "message": "Internal error" }`. The server logs the real error server-side; the response never includes internal detail (stack traces, driver text, etc.). 4xx errors are unaffected and keep their documented shapes.

## Fallback enum

One `fallbackReason` enum is shared by `/v1/refine`, `POST /v1/dictations` and stored dictation rows:

```
client-timeout | offline | unauthorized | server | llm-timeout | llm-error | llm-busy | llm-truncated | guard-rejected
```

`null` means no fallback happened (the LLM cleanup was used, or `mode: 'literal'` skipped it).

## Routes

| Method & path | Auth | Request | Response |
|---|---|---|---|
| `GET /health` | none | — | `200 { ok: true, version: string, db: 'ok'\|'error', llm: 'ok'\|'degraded'\|'unknown' }` — always 200 while the process is alive |
| `POST /v1/refine` | bearer | `{ clientId, raw, mode: 'clean'\|'literal', languageSetting?, languageDetected?, budgetMs?, context?: {appBundleId?, appName?}, timing: {audioMs, asrMs}, asrModel, clientVersion, createdAt }` | `200 { clientId, cleaned, raw, model: string\|null, llmMs: number, fallbackReason: <enum>\|null }` — on any LLM failure the server still answers 200 with `cleaned = raw` and a reason; the dictation row is upserted on `(user, clientId)` |
| `POST /v1/dictations` | bearer | `{ clientId, raw, injected: 'cleaned'\|'raw'\|'none'\|'clipboard', fallbackReason: <enum>\|null, mode, languageSetting, languageDetected?, context, timing, asrModel, clientVersion, createdAt, cleaned?, llmMs?, llmModel?, totalMs? }` — the last four carry cleanup that did **not** happen on `/v1/refine` (cleaned on the client, or skipped by the client-side gate, in which case `llmModel: 'skipped'`) plus the release→paste measurement; all four are nullable with `null` defaults so an older client still validates — **`asrModel` and `clientVersion` are required**, same as `/v1/refine` | `200 { id: string }` — outbox replay; upserts on `(user, clientId)`, never duplicates |
| `PATCH /v1/dictations/by-client/:clientId` | bearer | `{ injected: 'cleaned'\|'raw'\|'none'\|'clipboard', totalMs?: number\|null }` — `totalMs` rides the PATCH because release→paste is only known after the paste, which is after `/v1/refine` already wrote the row; `null` and absent both mean "not measured" and leave any stored value alone | `200 { ok: true }` · `404 { error: 'not_found', message }` when no dictation has that `clientId` for this user |
| `GET /v1/dictations?limit=&before=` | bearer | query `limit` (1-200, default 50), `before` (ms epoch, optional) | `200 { items: DictationRow[], nextBefore: number\|null }` — **`items` are the raw `dictations` table rows** (all columns: `id, userId, clientId, createdAt, raw, cleaned, injected, mode, languageSetting, languageDetected, appBundleId, appName, audioMs, asrMs, llmMs, totalMs, wordCount, asrModel, llmModel, clientVersion, fallbackReason`), newest first; `nextBefore` is the `createdAt` of the last row when a full page came back, else `null` — see the pagination note below |
| `GET /v1/insights?tz=&weeks=` | bearer | query `tz` (IANA name, default `UTC`), `weeks` (1-53, default 21) | `200 { totals: {dictations, words, audioMs, wpm: number\|null}, streak: {current, longest}, days: [{date: 'YYYY-MM-DD', dictations, words}], apps: [{label, dictations, share}], generatedAt }` · `400 { error: 'bad_request', message }` on an unknown `tz` or a `weeks` outside 1-53. **Numbers only — never `raw`, never `cleaned`, never a preview or a sample.** See the insights note below |
| `GET /v1/me` | bearer | — | `200 { user: {id, name}, settings: Settings, dictionary: {term, replacement}[], server: {version: string, model: string, concurrency: number, allowedModels: string[]} }` — `server.model` is the single active model (`env.llmModel()`), not a list; `server.allowedModels` is the full set `PUT /v1/settings.llmModel` will accept |
| `GET /v1/settings` | bearer | — | `200 Settings` |
| `PUT /v1/settings` | bearer | `Settings` | `200 Settings` (echoes what was stored) · `400` on an unknown enum value · `400 { error: 'unknown_model', message }` when `llmModel` is set but not in `server.allowedModels` (`/v1/me`) / `LLM_ALLOWED_MODELS` |
| `GET /v1/dictionary` | bearer | — | `200 Entry[]` — team-wide entries (`userId = null`) plus this user's own |
| `POST /v1/dictionary` | bearer | `{ term, replacement?, note?, teamWide? }` | `200 Entry` |
| `DELETE /v1/dictionary/:id` | bearer | — | `200 { ok: true }` · `404 { error: 'not_found', message }` when the id doesn't exist or belongs to another user's personal entry |

`Settings = { mode: 'clean' | 'literal'; language: 'auto' | 'pt' | 'en'; hotkey: 'fn' | 'rightOption' | 'rightCommand'; llmModel?: string }`. Defaults (no row stored yet): `{ mode: 'clean', language: 'auto', hotkey: 'fn' }`. When `llmModel` is set, `/v1/refine` uses it instead of the server's `LLM_MODEL` for that user, and it must be one of `server.allowedModels` (`/v1/me`) — anything else is a `400 unknown_model`. **To clear an override, OMIT `llmModel` from the `PUT` body** (it is an optional field); sending `llmModel: null` is a `400` validation error, not a way to clear it — the field is typed `string`, not `string | null`.

`Entry = { id: string; term: string; replacement: string | null; note: string | null; teamWide: boolean; createdAt: number }`.

`totalMs` is release→paste, measured on the Mac from the instant the hotkey is released to the instant the text reaches the caret. It deliberately includes ASR, cleanup, injection and the dispatch between them — it is **not** `asrMs + llmMs`, and the gap between those two numbers is the reason it is recorded separately. `llmModel` says which engine produced the text so the populations can be told apart: the server's model name, or `'skipped'` when the client's gate decided the transcript needed no cleanup at all.

`budgetMs` (on `/v1/refine`) is `1000`–`120000` ms, default `4000`. The server's own LLM-call timeout is derived from it, never sent by the client: `min(budgetMs − 700, LLM_MAX_TIMEOUT_MS)` — the 700 ms is headroom for the DB write and the response round-trip within the client's budget; `LLM_MAX_TIMEOUT_MS` (default 14000) is the hard ceiling regardless of how large `budgetMs` is.

**The upsert on `(user, clientId)` only ever updates `cleaned`, `injected`, `fallbackReason` (kept if already non-null), `llmMs`, `totalMs`, `llmModel`, `languageDetected` and `wordCount`.** Every other field — `raw`, `mode`, `languageSetting`, `context` (`appBundleId`/`appName`), `timing` (`audioMs`/`asrMs`), `asrModel`, `clientVersion`, `createdAt` — is frozen at whatever the *first* successful insert for that `clientId` wrote; a later `/v1/refine` or `/v1/dictations` call for the same `clientId` cannot change them even if the payload differs.

`wordCount` is the number of whitespace-separated tokens in the text that was actually pasted — `cleaned` when there is one, `raw` otherwise. It is written by the same single upsert statement as the row, never read back and recomputed, and the conflict clause is `word_count = CASE WHEN excluded.cleaned IS NOT NULL THEN excluded.word_count ELSE dictations.word_count END`. The `ELSE` matters: an outbox replay legitimately carries `cleaned: null` for a dictation the server had already cleaned, and without it the raw count would overwrite the cleaned one and inflate the user's word total by every filler the cleanup removed. The column is **nullable** — `null` means "written before the column existed, not yet counted", is excluded from word totals rather than summed as zero, and is filled in by a backfill that runs once at boot.

**Insights note.** `GET /v1/insights` returns aggregates and nothing else; the handler never selects `raw` or `cleaned`, which is what makes that structural rather than a matter of review. Details that are easy to get wrong:

- **`tz` is validated by trying to build an `Intl.DateTimeFormat` for it**, not against `Intl.supportedValuesOf('timeZone')` — that list holds only canonical zones (418 here) and excludes `UTC`, every `Etc/*` and every alias such as `Asia/Calcutta`. An unknown zone is a `400`, never a silent fallback to UTC, because a typo that quietly buckets days in UTC produces a plausible wrong streak that nobody would catch.
- **Day bucketing happens in Node, not SQLite**, which has no timezone database; a fixed UTC offset is wrong for every historical day on the far side of a DST change.
- **`streak` is computed over all history, `days` only over `weeks`.** A 60-day streak viewed through a 21-week window still reads 60. `current` counts back from today, or from yesterday when today is empty — otherwise it would read 0 every morning until the first dictation.
- **`days` covers `weeks` complete Sun–Sat weeks plus the current partial week, zero-filled with no gaps.** Every date in the range is present even when nothing was dictated, so a client never has to infer a missing day.
- **`wpm` is `totalWords / (totalAudioMs / 60000)` over rows with `audio_ms > 0`, rounded**, and `null` below 60 000 ms of total audio — it is speaking rate, and a single three-second dictation yields a number like 340 that is arithmetically true and meaningless.
- **`apps` is the top 6 labels by dictation count plus `Other`**, which is omitted entirely when nothing overflows into it. The label is `appName`, falling back to `appBundleId`, falling back to `Unknown`; rows that collapse to the same label are merged. `share` values are integers rounded by largest remainder so they sum to exactly 100.

**Pagination note:** `before` is `createdAt < before` (strictly less than), not `<=`. If two dictations from the same user land on the exact same millisecond `createdAt`, a page boundary that falls between them will skip whichever one sorts before the cutoff — clients must mint a unique per-user, millisecond `createdAt` for every dictation (e.g. bump by 1 ms on collision) or pagination can silently drop rows. (Deferred: a keyset over `(createdAt, id)` instead of `createdAt` alone would remove this requirement.)

## curl examples

Set `TOKEN` once: `TOKEN=mv_...`

**`GET /health`**
```
curl -s https://voice.miraside.co/health
```

**`POST /v1/refine`**
```
curl -s -X POST https://voice.miraside.co/v1/refine \
  -H "Authorization: Bearer $TOKEN" -H 'content-type: application/json' \
  -d '{"clientId":"11111111-1111-4111-8111-111111111111","raw":"hã então eu acho que que amanhã vamos vamos fechar o contrato tipo às três","mode":"clean","languageSetting":"auto","languageDetected":"pt","budgetMs":4000,"context":{},"timing":{"audioMs":6000,"asrMs":1400},"asrModel":"large-v3-turbo-632MB","clientVersion":"0.1.0","createdAt":1757400000000}'
```

**`POST /v1/dictations`** (outbox replay)
```
curl -s -X POST https://voice.miraside.co/v1/dictations \
  -H "Authorization: Bearer $TOKEN" -H 'content-type: application/json' \
  -d '{"clientId":"11111111-1111-4111-8111-111111111111","raw":"texto ditado","injected":"raw","fallbackReason":"offline","mode":"clean","languageSetting":"auto","context":{},"timing":{"audioMs":3000,"asrMs":900},"asrModel":"large-v3-turbo-632MB","clientVersion":"0.1.0","createdAt":1757400000000}'
```

**`GET /v1/insights`**
```
curl -s "https://voice.miraside.co/v1/insights?tz=Europe/Lisbon&weeks=21" \
  -H "Authorization: Bearer $TOKEN"
```
Returns numbers only. A quick way to prove that, against your own data:
```
curl -s "https://voice.miraside.co/v1/insights?tz=Europe/Lisbon" -H "Authorization: Bearer $TOKEN" \
  | grep -ci 'raw\|cleaned'   # must print 0
```

**`PATCH /v1/dictations/by-client/:clientId`**
```
curl -s -X PATCH https://voice.miraside.co/v1/dictations/by-client/11111111-1111-4111-8111-111111111111 \
  -H "Authorization: Bearer $TOKEN" -H 'content-type: application/json' \
  -d '{"injected":"cleaned"}'
```

**`GET /v1/dictations`**
```
curl -s "https://voice.miraside.co/v1/dictations?limit=50" -H "Authorization: Bearer $TOKEN"
```

**`GET /v1/me`**
```
curl -s https://voice.miraside.co/v1/me -H "Authorization: Bearer $TOKEN"
```

**`GET /v1/settings`**
```
curl -s https://voice.miraside.co/v1/settings -H "Authorization: Bearer $TOKEN"
```

**`PUT /v1/settings`**
```
curl -s -X PUT https://voice.miraside.co/v1/settings \
  -H "Authorization: Bearer $TOKEN" -H 'content-type: application/json' \
  -d '{"mode":"literal","language":"pt","hotkey":"rightOption","llmModel":"gpt-oss:120b"}'
```

**`GET /v1/dictionary`**
```
curl -s https://voice.miraside.co/v1/dictionary -H "Authorization: Bearer $TOKEN"
```

**`POST /v1/dictionary`**
```
curl -s -X POST https://voice.miraside.co/v1/dictionary \
  -H "Authorization: Bearer $TOKEN" -H 'content-type: application/json' \
  -d '{"term":"Miraside","teamWide":true}'
```

**`DELETE /v1/dictionary/:id`**
```
curl -s -X DELETE https://voice.miraside.co/v1/dictionary/<id> -H "Authorization: Bearer $TOKEN"
```

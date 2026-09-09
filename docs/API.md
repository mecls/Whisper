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
| `POST /v1/dictations` | bearer | `{ clientId, raw, injected: 'cleaned'\|'raw'\|'none'\|'clipboard', fallbackReason: <enum>\|null, mode, languageSetting, languageDetected?, context, timing, asrModel, clientVersion, createdAt }` — **`asrModel` and `clientVersion` are required**, same as `/v1/refine` | `200 { id: string }` — outbox replay; upserts on `(user, clientId)`, never duplicates |
| `PATCH /v1/dictations/by-client/:clientId` | bearer | `{ injected: 'cleaned'\|'raw'\|'none'\|'clipboard' }` | `200 { ok: true }` · `404 { error: 'not_found', message }` when no dictation has that `clientId` for this user |
| `GET /v1/dictations?limit=&before=` | bearer | query `limit` (1-200, default 50), `before` (ms epoch, optional) | `200 { items: DictationRow[], nextBefore: number\|null }` — **`items` are the raw `dictations` table rows** (all columns: `id, userId, clientId, createdAt, raw, cleaned, injected, mode, languageSetting, languageDetected, appBundleId, appName, audioMs, asrMs, llmMs, asrModel, llmModel, clientVersion, fallbackReason`), newest first; `nextBefore` is the `createdAt` of the last row when a full page came back, else `null` — see the pagination note below |
| `GET /v1/me` | bearer | — | `200 { user: {id, name}, settings: Settings, dictionary: {term, replacement}[], server: {version: string, model: string, concurrency: number, allowedModels: string[]} }` — `server.model` is the single active model (`env.llmModel()`), not a list; `server.allowedModels` is the full set `PUT /v1/settings.llmModel` will accept |
| `GET /v1/settings` | bearer | — | `200 Settings` |
| `PUT /v1/settings` | bearer | `Settings` | `200 Settings` (echoes what was stored) · `400` on an unknown enum value · `400 { error: 'unknown_model', message }` when `llmModel` is set but not in `server.allowedModels` (`/v1/me`) / `LLM_ALLOWED_MODELS` |
| `GET /v1/dictionary` | bearer | — | `200 Entry[]` — team-wide entries (`userId = null`) plus this user's own |
| `POST /v1/dictionary` | bearer | `{ term, replacement?, note?, teamWide? }` | `200 Entry` |
| `DELETE /v1/dictionary/:id` | bearer | — | `200 { ok: true }` · `404 { error: 'not_found', message }` when the id doesn't exist or belongs to another user's personal entry |

`Settings = { mode: 'clean' | 'literal'; language: 'auto' | 'pt' | 'en'; hotkey: 'fn' | 'rightOption' | 'rightCommand'; llmModel?: string }`. Defaults (no row stored yet): `{ mode: 'clean', language: 'auto', hotkey: 'fn' }`. When `llmModel` is set, `/v1/refine` uses it instead of the server's `LLM_MODEL` for that user, and it must be one of `server.allowedModels` (`/v1/me`) — anything else is a `400 unknown_model`. **To clear an override, OMIT `llmModel` from the `PUT` body** (it is an optional field); sending `llmModel: null` is a `400` validation error, not a way to clear it — the field is typed `string`, not `string | null`.

`Entry = { id: string; term: string; replacement: string | null; note: string | null; teamWide: boolean; createdAt: number }`.

`budgetMs` (on `/v1/refine`) is `1000`–`120000` ms, default `4000`. The server's own LLM-call timeout is derived from it, never sent by the client: `min(budgetMs − 700, LLM_MAX_TIMEOUT_MS)` — the 700 ms is headroom for the DB write and the response round-trip within the client's budget; `LLM_MAX_TIMEOUT_MS` (default 14000) is the hard ceiling regardless of how large `budgetMs` is.

**The upsert on `(user, clientId)` only ever updates `cleaned`, `injected`, `fallbackReason` (kept if already non-null), `llmMs`, `llmModel` and `languageDetected`.** Every other field — `raw`, `mode`, `languageSetting`, `context` (`appBundleId`/`appName`), `timing` (`audioMs`/`asrMs`), `asrModel`, `clientVersion`, `createdAt` — is frozen at whatever the *first* successful insert for that `clientId` wrote; a later `/v1/refine` or `/v1/dictations` call for the same `clientId` cannot change them even if the payload differs.

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

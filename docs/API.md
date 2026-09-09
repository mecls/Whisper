# Miraside Voice API

Base URL: `https://voice.miraside.co`. All request/response bodies are JSON. Every response echoes the request id on `X-Request-Id`.

## Auth

Every route except `GET /health` requires a bearer device token, minted once by `node dist/cli/users.js add "<name>" --label "<device>"`:

```
Authorization: Bearer mv_<32 random bytes, base64url>
```

Missing, malformed, revoked or unknown tokens, and tokens for a disabled user, get `401` with `{ "error": "missing_token" | "invalid_token" | "token_revoked" | "user_disabled", "message": "Authentication failed" }`. A route-level `404` (e.g. deleting someone else's dictionary entry) uses the same `{ error, message }` shape. Body-validation failures (Zod, via `fastify-type-provider-zod`) return `400` in Fastify's own shape instead: `{ "statusCode": 400, "code": "FST_ERR_VALIDATION", "error": "Bad Request", "message": "body/mode Invalid option: ..." }`. Rate limiting (120 req/min per token, plus a per-IP cap on failed auth) returns `429`.

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
| `POST /v1/dictations` | bearer | `{ clientId, raw, injected: 'cleaned'\|'raw'\|'none'\|'clipboard', fallbackReason: <enum>\|null, mode, languageSetting, languageDetected?, context, timing, asrModel, clientVersion, createdAt }` | `200 { id: string }` — outbox replay; upserts on `(user, clientId)`, never duplicates |
| `PATCH /v1/dictations/by-client/:clientId` | bearer | `{ injected: 'cleaned'\|'raw'\|'none'\|'clipboard' }` | `200 { ok: true }` · `404 { error: 'not_found', message }` when no dictation has that `clientId` for this user |
| `GET /v1/dictations?limit=&before=` | bearer | query `limit` (1-200, default 50), `before` (ms epoch, optional) | `200 { items: DictationRow[], nextBefore: number\|null }` — **`items` are the raw `dictations` table rows** (all columns: `id, userId, clientId, createdAt, raw, cleaned, injected, mode, languageSetting, languageDetected, appBundleId, appName, audioMs, asrMs, llmMs, asrModel, llmModel, clientVersion, fallbackReason`), newest first; `nextBefore` is the `createdAt` of the last row when a full page came back, else `null` |
| `GET /v1/me` | bearer | — | `200 { user: {id, name}, settings: Settings, dictionary: {term, replacement}[], server: {version: string, model: string, concurrency: number} }` — `server.model` is the single active model (`env.llmModel()`), not a list |
| `GET /v1/settings` | bearer | — | `200 Settings` |
| `PUT /v1/settings` | bearer | `Settings` | `200 Settings` (echoes what was stored) · `400` on an unknown enum value |
| `GET /v1/dictionary` | bearer | — | `200 Entry[]` — team-wide entries (`userId = null`) plus this user's own |
| `POST /v1/dictionary` | bearer | `{ term, replacement?, note?, teamWide? }` | `200 Entry` |
| `DELETE /v1/dictionary/:id` | bearer | — | `200 { ok: true }` · `404 { error: 'not_found', message }` when the id doesn't exist or belongs to another user's personal entry |

`Settings = { mode: 'clean' | 'literal'; language: 'auto' | 'pt' | 'en'; hotkey: 'fn' | 'rightOption' | 'rightCommand'; llmModel?: string }`. Defaults (no row stored yet): `{ mode: 'clean', language: 'auto', hotkey: 'fn' }`. When `llmModel` is set, `/v1/refine` uses it instead of the server's `LLM_MODEL` for that user.

`Entry = { id: string; term: string; replacement: string | null; note: string | null; teamWide: boolean; createdAt: number }`.

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

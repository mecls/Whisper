# Miraside Voice — Server Implementation Plan (M0 + M1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A small HTTPS API on the VPS that authenticates Mac clients with device tokens, cleans raw dictation text through Ollama Cloud, and stores text-only history, settings and a dictionary in SQLite.

**Architecture:** One Node 22 process (Fastify 5) behind the VPS's existing Traefik, SQLite on a Docker volume, the `openai` SDK pointed at `https://ollama.com/v1`. Every route is a thin handler over three pure modules: `auth` (token → user), `llm/refine` (prompt + output guards, LLM injected), `db` (Drizzle over better-sqlite3). `buildApp(deps)` takes its dependencies so tests inject an in-memory DB and a fake LLM.

**Tech Stack:** Node ≥22.11, TypeScript 5.9 (strict, NodeNext ESM), Fastify 5.12 + fastify-type-provider-zod 7 + zod 4, better-sqlite3 13 + drizzle-orm 0.45, openai 7, pino 10, node:test via tsx. Docker (node:22-bookworm builder → node:22-bookworm-slim runtime) behind the VPS's existing Traefik.

**Spec:** `docs/PLAN.md` (sections 4, 5, 6, 7, 8 M0–M1, 9). Measurements: `docs/SPIKES.md`.

## Global Constraints

- Node `>=22.11`; npm (no pnpm/bun); `package-lock.json` committed.
- Transcript text is never logged. `LOG_TRANSCRIPTS=1` is refused when `NODE_ENV=production`.
- Every route except `GET /health` requires `Authorization: Bearer mv_…`; body limit 64 KB; rate limit keyed by the token hash.
- Timestamps are ms epoch integers. Ids are nanoid (21 chars). The dictation idempotency key is `(user_id, client_id)`.
- The single fallback enum: `client-timeout | offline | unauthorized | server | llm-timeout | llm-error | llm-busy | llm-truncated | guard-rejected`.
- Defaults from the spike: `LLM_MODEL=gemma4`, `LLM_REASONING=none`, `LLM_CONCURRENCY=5`, `max_tokens = max(512, estTokens × 3 + 192)`.
- ESM with `.js` extensions on relative imports (NodeNext).
- Commit after every task with a conventional message; never commit `.env`.

---

### Task 1: Server scaffold, env, `/health`

**Files:**
- Create: `server/package.json`, `server/tsconfig.json`, `server/.env.example`, `server/src/env.ts`, `server/src/app.ts`, `server/src/index.ts`
- Test: `server/test/env.test.ts`, `server/test/health.test.ts`

**Interfaces:**
- Produces: `env` object with lazy getters (`env.llmBaseUrl()`, `env.llmApiKey()`, `env.llmModel()`, `env.llmReasoning()`, `env.llmConcurrency()`, `env.llmMaxTimeoutMs()`, `env.databasePath()`, `env.port()`, `env.logTranscripts()`); `buildApp(deps: AppDeps): FastifyInstance` where `AppDeps = { db: Db; llm: LlmCaller; version: string }` (the `db` and `llm` types arrive in Tasks 2 and 4; Task 1 declares them as `unknown` placeholders replaced later — see Step 3).

- [ ] **Step 1: Create `server/package.json` and `server/tsconfig.json`**

```json
{
  "name": "miraside-voice-server",
  "version": "0.1.0",
  "private": true,
  "type": "module",
  "engines": { "node": ">=22.11" },
  "scripts": {
    "dev": "tsx watch src/index.ts",
    "build": "tsc -p tsconfig.json",
    "start": "node dist/index.js",
    "typecheck": "tsc --noEmit",
    "test": "tsx --test test/*.test.ts",
    "migrate": "tsx src/db/migrate.ts",
    "users": "tsx src/cli/users.ts",
    "bench": "tsx src/cli/bench.ts"
  },
  "dependencies": {
    "@fastify/helmet": "^13.1.1",
    "@fastify/rate-limit": "^11.2.0",
    "better-sqlite3": "^13.0.3",
    "drizzle-orm": "^0.45.2",
    "fastify": "^5.12.3",
    "fastify-type-provider-zod": "^7.0.0",
    "nanoid": "^6.0.1",
    "openai": "^7.12.1",
    "pino": "^10.3.1",
    "zod": "^4.5.4"
  },
  "devDependencies": {
    "@types/better-sqlite3": "^9.6.0",
    "@types/node": "^22.20.1",
    "pino-pretty": "^13.1.3",
    "tsx": "^4.23.13",
    "typescript": "^5.9.3"
  }
}
```

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "NodeNext",
    "moduleResolution": "NodeNext",
    "strict": true,
    "esModuleInterop": true,
    "skipLibCheck": true,
    "outDir": "dist",
    "rootDir": "src",
    "types": ["node"],
    "resolveJsonModule": true
  },
  "include": ["src"]
}
```

Run: `cd server && npm install`
Expected: `package-lock.json` created, no peer warnings about zod (fastify-type-provider-zod 7 needs zod ≥ 4.1.5).

- [ ] **Step 2: Write the failing env test**

`server/test/env.test.ts`:
```ts
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { env } from '../src/env.js'

test('required vars throw a named error when missing', () => {
  delete process.env.LLM_API_KEY
  assert.throws(() => env.llmApiKey(), /Missing required environment variable: LLM_API_KEY/)
})

test('defaults come from the spike', () => {
  delete process.env.LLM_MODEL
  delete process.env.LLM_REASONING
  delete process.env.LLM_CONCURRENCY
  assert.equal(env.llmModel(), 'gemma4')
  assert.equal(env.llmReasoning(), 'none')
  assert.equal(env.llmConcurrency(), 5)
  assert.equal(env.llmMaxTimeoutMs(), 14000)
  assert.equal(env.port(), 8080)
})

test('LOG_TRANSCRIPTS is refused in production', () => {
  process.env.NODE_ENV = 'production'
  process.env.LOG_TRANSCRIPTS = '1'
  assert.throws(() => env.logTranscripts(), /LOG_TRANSCRIPTS is not allowed in production/)
  process.env.NODE_ENV = 'test'
  assert.equal(env.logTranscripts(), true)
  process.env.LOG_TRANSCRIPTS = '0'
  assert.equal(env.logTranscripts(), false)
})
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `cd server && npm test`
Expected: FAIL — `Cannot find module '../src/env.js'`.

- [ ] **Step 4: Write `server/src/env.ts`**

```ts
/**
 * Typed environment access (pattern from hub/LoanAgent/lib/env.ts).
 *
 * Values are read lazily through getters that throw a clear error when
 * missing, so a missing key surfaces at the call site, not as `undefined`
 * deep inside the SDK.
 */
function required(name: string): string {
  const v = process.env[name]
  if (!v) throw new Error(`Missing required environment variable: ${name}`)
  return v
}

function intOr(name: string, fallback: number): number {
  const v = process.env[name]
  if (!v) return fallback
  const n = Number.parseInt(v, 10)
  if (!Number.isFinite(n)) throw new Error(`Environment variable ${name} must be an integer, got ${JSON.stringify(v)}`)
  return n
}

export const env = {
  llmBaseUrl: () => process.env.LLM_BASE_URL ?? 'https://ollama.com/v1',
  llmApiKey: () => required('LLM_API_KEY'),
  // Spike (docs/SPIKES.md): gemma4 at `none` was the fastest clean model.
  llmModel: () => process.env.LLM_MODEL ?? 'gemma4',
  llmReasoning: () => process.env.LLM_REASONING ?? 'none',
  // Ollama Cloud caps in-flight requests per plan; the account handled 5.
  llmConcurrency: () => intOr('LLM_CONCURRENCY', 5),
  // Ceiling for the per-request budget the Mac sends.
  llmMaxTimeoutMs: () => intOr('LLM_MAX_TIMEOUT_MS', 14000),
  databasePath: () => process.env.DATABASE_PATH ?? './data/voice.db',
  port: () => intOr('PORT', 8080),
  /** Transcripts in logs are a dev-only aid; production refuses the flag outright. */
  logTranscripts: () => {
    const on = process.env.LOG_TRANSCRIPTS === '1'
    if (on && process.env.NODE_ENV === 'production') {
      throw new Error('LOG_TRANSCRIPTS is not allowed in production')
    }
    return on
  },
} as const
```

- [ ] **Step 5: Run the env test to verify it passes**

Run: `cd server && npm test`
Expected: 3 passing.

- [ ] **Step 6: Write the failing health test**

`server/test/health.test.ts`:
```ts
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { buildApp } from '../src/app.js'

test('GET /health is public and always 200', async () => {
  const app = buildApp({ db: null as never, llm: null as never, version: 'test' })
  const res = await app.inject({ method: 'GET', url: '/health' })
  assert.equal(res.statusCode, 200)
  const body = res.json()
  assert.equal(body.ok, true)
  assert.equal(body.version, 'test')
  assert.equal(body.db, 'ok')
  await app.close()
})
```

- [ ] **Step 7: Write `server/src/app.ts` and `server/src/index.ts`**

`server/src/app.ts` (the `db`/`llm` fields are typed as `unknown` for now; Tasks 2 and 4 replace them):
```ts
import Fastify, { type FastifyInstance } from 'fastify'
import helmet from '@fastify/helmet'
import { serializerCompiler, validatorCompiler, type ZodTypeProvider } from 'fastify-type-provider-zod'
import { env } from './env.js'

export interface AppDeps {
  db: unknown
  llm: unknown
  version: string
}

export function buildApp(deps: AppDeps): FastifyInstance {
  const app = Fastify({
    logger: {
      level: process.env.LOG_LEVEL ?? 'info',
      // Never let a transcript into the log through a serialized request body.
      serializers: { req: (r) => ({ method: r.method, url: r.url, id: r.id }) },
    },
    bodyLimit: 64 * 1024,
    requestIdHeader: 'x-request-id',
    disableRequestLogging: false,
  })
  app.setValidatorCompiler(validatorCompiler)
  app.setSerializerCompiler(serializerCompiler)
  app.register(helmet, { global: true })
  app.addHook('onSend', async (req, reply) => {
    reply.header('x-request-id', req.id)
  })

  app.withTypeProvider<ZodTypeProvider>().get('/health', async () => ({
    ok: true,
    version: deps.version,
    db: 'ok',
    llm: 'unknown',
  }))

  return app
}

export type App = ReturnType<typeof buildApp>
export { env }
```

`server/src/index.ts`:
```ts
import { readFileSync } from 'node:fs'
import { buildApp } from './app.js'
import { env } from './env.js'

const version = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8')).version as string
const app = buildApp({ db: null, llm: null, version })

const shutdown = async (signal: string) => {
  app.log.info({ signal }, 'shutting down')
  await app.close()
  process.exit(0)
}
process.on('SIGTERM', () => void shutdown('SIGTERM'))
process.on('SIGINT', () => void shutdown('SIGINT'))

app.listen({ port: env.port(), host: '0.0.0.0' }).catch((err) => {
  app.log.error(err)
  process.exit(1)
})
```

- [ ] **Step 8: Run tests and typecheck**

Run: `cd server && npm test && npm run typecheck`
Expected: 4 passing, no type errors.

- [ ] **Step 9: Write `server/.env.example`**

```
# ─── LLM (Ollama Cloud through its OpenAI-compatible endpoint) ───────────────
# One shared key, kept only here on the VPS; Macs never see it.
LLM_BASE_URL=https://ollama.com/v1
LLM_API_KEY=
# docs/SPIKES.md: gemma4 at `none` cleaned all fixtures in ~0.7 s with no reasoning tokens.
LLM_MODEL=gemma4
LLM_REASONING=none              # none|low|medium|high — gpt-oss needs low (it ignores none)
LLM_CONCURRENCY=5               # Ollama caps in-flight requests per plan; extra requests fail fast
LLM_MAX_TIMEOUT_MS=14000        # ceiling for the per-request budget the Mac sends
# ─── Storage ─────────────────────────────────────────────────────────────────
DATABASE_PATH=/data/voice.db
# ─── Server ──────────────────────────────────────────────────────────────────
PORT=8080
LOG_LEVEL=info
LOG_TRANSCRIPTS=0               # refused in production
```

- [ ] **Step 10: Commit**

```bash
cd /Users/miguelcarvalhal/Documents/Projects/SintraLabs/apps/voice
git add .gitignore docs server/package.json server/package-lock.json server/tsconfig.json server/.env.example server/src server/test
git commit -m "feat(server): scaffold Fastify app with typed env and /health"
```

---

### Task 2: SQLite schema, client and migrations

**Files:**
- Create: `server/src/db/schema.ts`, `server/src/db/client.ts`, `server/src/db/migrate.ts`, `server/src/db/migrations/0001_init.sql`
- Modify: `server/src/app.ts` (replace `db: unknown` with `Db`)
- Test: `server/test/db.test.ts`

**Interfaces:**
- Produces: `openDb(path: string): Db` (applies pending migrations; `':memory:'` for tests), `type Db = BetterSQLite3Database<typeof schema> & { raw: Database.Database }`, the Drizzle tables `users`, `deviceTokens`, `dictations`, `userSettings`, `dictionaryEntries`, and `now(): number` (ms).

- [ ] **Step 1: Write the failing DB test**

`server/test/db.test.ts`:
```ts
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { eq } from 'drizzle-orm'
import { openDb } from '../src/db/client.js'
import { users, dictations } from '../src/db/schema.js'

test('migrations apply and dictations enforce (user, client_id) uniqueness', () => {
  const db = openDb(':memory:')
  const tables = db.raw.prepare("select name from sqlite_master where type='table' order by name").all() as { name: string }[]
  assert.deepEqual(
    tables.map((t) => t.name).filter((n) => !n.startsWith('sqlite_')),
    ['_migrations', 'device_tokens', 'dictationary_never'].length === 0 ? [] : ['_migrations', 'device_tokens', 'dictations', 'dictionary_entries', 'user_settings', 'users'],
  )
  db.insert(users).values({ id: 'u1', name: 'Miguel', createdAt: 1 }).run()
  const row = { id: 'd1', userId: 'u1', clientId: 'c1', createdAt: 1, raw: 'olá', mode: 'clean', languageSetting: 'auto' }
  db.insert(dictations).values(row).run()
  assert.throws(() => db.insert(dictations).values({ ...row, id: 'd2' }).run(), /UNIQUE/)
  const got = db.select().from(dictations).where(eq(dictations.clientId, 'c1')).get()
  assert.equal(got?.injected, null)
  assert.equal(got?.cleaned, null)
})

test('opening twice does not re-run migrations', () => {
  const db = openDb(':memory:')
  const n = db.raw.prepare('select count(*) as n from _migrations').get() as { n: number }
  assert.equal(n.n, 1)
})
```

(The odd-looking `assert.deepEqual` line is a plain list comparison; simplify it to the second array literal — it is written out here so the expected table list is explicit: `_migrations, device_tokens, dictations, dictionary_entries, user_settings, users`.)

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd server && npm test`
Expected: FAIL — cannot find `../src/db/client.js`.

- [ ] **Step 3: Write the schema**

`server/src/db/schema.ts`:
```ts
import { sqliteTable, text, integer, index, uniqueIndex } from 'drizzle-orm/sqlite-core'

// Timestamps are ms epoch integers (house convention). Ids are nanoid.

export const users = sqliteTable('users', {
  id: text('id').primaryKey(),
  name: text('name').notNull(),
  createdAt: integer('created_at').notNull(),
  disabledAt: integer('disabled_at'),
})

export const deviceTokens = sqliteTable(
  'device_tokens',
  {
    id: text('id').primaryKey(),
    userId: text('user_id').notNull().references(() => users.id),
    tokenHash: text('token_hash').notNull(),
    label: text('label').notNull(),
    createdAt: integer('created_at').notNull(),
    lastUsedAt: integer('last_used_at'),
    revokedAt: integer('revoked_at'),
  },
  (t) => [uniqueIndex('device_tokens_hash').on(t.tokenHash)],
)

export const FALLBACK_REASONS = [
  'client-timeout', 'offline', 'unauthorized', 'server',
  'llm-timeout', 'llm-error', 'llm-busy', 'llm-truncated', 'guard-rejected',
] as const
export type FallbackReason = (typeof FALLBACK_REASONS)[number]

export const INJECTED = ['cleaned', 'raw', 'none', 'clipboard'] as const
export type Injected = (typeof INJECTED)[number]

export const dictations = sqliteTable(
  'dictations',
  {
    id: text('id').primaryKey(),
    userId: text('user_id').notNull().references(() => users.id),
    clientId: text('client_id').notNull(),
    createdAt: integer('created_at').notNull(),
    raw: text('raw').notNull(),
    cleaned: text('cleaned'),
    injected: text('injected', { enum: INJECTED }),
    mode: text('mode').notNull(),
    languageSetting: text('language_setting').notNull(),
    languageDetected: text('language_detected'),
    appBundleId: text('app_bundle_id'),
    appName: text('app_name'),
    audioMs: integer('audio_ms'),
    asrMs: integer('asr_ms'),
    llmMs: integer('llm_ms'),
    asrModel: text('asr_model'),
    llmModel: text('llm_model'),
    clientVersion: text('client_version'),
    fallbackReason: text('fallback_reason', { enum: FALLBACK_REASONS }),
  },
  (t) => [
    uniqueIndex('dictations_user_client').on(t.userId, t.clientId),
    index('dictations_user_created').on(t.userId, t.createdAt),
  ],
)

export const userSettings = sqliteTable('user_settings', {
  userId: text('user_id').primaryKey().references(() => users.id),
  json: text('json').notNull(),
  updatedAt: integer('updated_at').notNull(),
})

export const dictionaryEntries = sqliteTable(
  'dictionary_entries',
  {
    id: text('id').primaryKey(),
    userId: text('user_id').references(() => users.id), // null = team-wide
    term: text('term').notNull(),
    replacement: text('replacement'),
    note: text('note'),
    createdAt: integer('created_at').notNull(),
  },
  (t) => [index('dictionary_user').on(t.userId)],
)

export const now = (): number => Date.now()
```

- [ ] **Step 4: Write the migration SQL**

`server/src/db/migrations/0001_init.sql`:
```sql
create table users (
  id text primary key,
  name text not null,
  created_at integer not null,
  disabled_at integer
);
create table device_tokens (
  id text primary key,
  user_id text not null references users(id),
  token_hash text not null,
  label text not null,
  created_at integer not null,
  last_used_at integer,
  revoked_at integer
);
create unique index device_tokens_hash on device_tokens(token_hash);
create table dictations (
  id text primary key,
  user_id text not null references users(id),
  client_id text not null,
  created_at integer not null,
  raw text not null,
  cleaned text,
  injected text,
  mode text not null,
  language_setting text not null,
  language_detected text,
  app_bundle_id text,
  app_name text,
  audio_ms integer,
  asr_ms integer,
  llm_ms integer,
  asr_model text,
  llm_model text,
  client_version text,
  fallback_reason text
);
create unique index dictations_user_client on dictations(user_id, client_id);
create index dictations_user_created on dictations(user_id, created_at desc);
create table user_settings (
  user_id text primary key references users(id),
  json text not null,
  updated_at integer not null
);
create table dictionary_entries (
  id text primary key,
  user_id text references users(id),
  term text not null,
  replacement text,
  note text,
  created_at integer not null
);
create index dictionary_user on dictionary_entries(user_id);
```

- [ ] **Step 5: Write the client and the migration runner**

`server/src/db/client.ts`:
```ts
import Database from 'better-sqlite3'
import { drizzle, type BetterSQLite3Database } from 'drizzle-orm/better-sqlite3'
import { mkdirSync } from 'node:fs'
import { dirname } from 'node:path'
import * as schema from './schema.js'
import { applyMigrations } from './migrate.js'

export type Db = BetterSQLite3Database<typeof schema> & { raw: Database.Database }

/** Opens (creating if needed) the SQLite file, sets WAL, applies pending migrations. */
export function openDb(path: string): Db {
  if (path !== ':memory:') mkdirSync(dirname(path), { recursive: true })
  const raw = new Database(path)
  raw.pragma('journal_mode = WAL')
  raw.pragma('foreign_keys = ON')
  raw.pragma('busy_timeout = 5000')
  applyMigrations(raw)
  const db = drizzle(raw, { schema }) as Db
  db.raw = raw
  return db
}
```

`server/src/db/migrate.ts`:
```ts
import type Database from 'better-sqlite3'
import { readdirSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

const MIGRATIONS_DIR = join(dirname(fileURLToPath(import.meta.url)), 'migrations')

/** Hand-written SQL files applied once each, in name order, tracked in `_migrations`. */
export function applyMigrations(raw: Database.Database): string[] {
  raw.exec('create table if not exists _migrations (name text primary key, applied_at integer not null)')
  const applied = new Set((raw.prepare('select name from _migrations').all() as { name: string }[]).map((r) => r.name))
  const files = readdirSync(MIGRATIONS_DIR).filter((f) => f.endsWith('.sql')).sort()
  const ran: string[] = []
  for (const f of files) {
    if (applied.has(f)) continue
    const sql = readFileSync(join(MIGRATIONS_DIR, f), 'utf8')
    raw.transaction(() => {
      raw.exec(sql)
      raw.prepare('insert into _migrations (name, applied_at) values (?, ?)').run(f, Date.now())
    })()
    ran.push(f)
  }
  return ran
}

// `npm run migrate` — apply to the configured database and exit.
if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const { openDb } = await import('./client.js')
  const { env } = await import('../env.js')
  const db = openDb(env.databasePath())
  console.log(`database ${env.databasePath()} up to date`)
  db.raw.close()
}
```

Note for the tsconfig: the build must copy `migrations/*.sql` into `dist/` — add to `package.json` scripts: `"build": "tsc -p tsconfig.json && cp -R src/db/migrations dist/db/migrations"`.

- [ ] **Step 6: Wire `Db` into `AppDeps`**

In `server/src/app.ts` replace `db: unknown` with `db: Db` (import `type Db` from `./db/client.js`) and make `/health` run `deps.db.raw.prepare('select 1').get()` inside a try/catch, reporting `db: 'ok' | 'error'`. Update `test/health.test.ts` to pass `db: openDb(':memory:')`. In `index.ts` call `openDb(env.databasePath())`.

- [ ] **Step 7: Run tests and typecheck**

Run: `cd server && npm test && npm run typecheck && npm run build && ls dist/db/migrations`
Expected: all passing; `0001_init.sql` present in `dist`.

- [ ] **Step 8: Commit**

```bash
git add server/src/db server/src/app.ts server/src/index.ts server/test/db.test.ts server/test/health.test.ts server/package.json
git commit -m "feat(server): SQLite schema, WAL client and SQL migrations"
```

---

### Task 3: Device tokens, users CLI and the auth hook

**Files:**
- Create: `server/src/auth.ts`, `server/src/cli/users.ts`
- Modify: `server/src/app.ts` (register the hook; decorate `request.user`)
- Test: `server/test/auth.test.ts`

**Interfaces:**
- Produces: `generateToken(): { token: string; hash: string }`, `hashToken(token: string): string`, `createUser(db, name): User`, `issueToken(db, userId, label): string`, `revokeToken(db, tokenId)`, `authenticate(db, header: string | undefined, nowMs?: number): AuthResult` where `AuthResult = { ok: true; user: { id: string; name: string }; tokenId: string } | { ok: false; error: 'missing_token' | 'invalid_token' | 'token_revoked' | 'user_disabled' }`; Fastify decorator `request.user` set by an `onRequest` hook for every route except `/health`.

- [ ] **Step 1: Write the failing auth tests**

`server/test/auth.test.ts`:
```ts
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { openDb } from '../src/db/client.js'
import { createUser, issueToken, revokeToken, authenticate, hashToken, generateToken } from '../src/auth.js'
import { deviceTokens, users } from '../src/db/schema.js'
import { eq } from 'drizzle-orm'

test('tokens are mv_ prefixed, 43 chars of base64url, stored only as sha256', () => {
  const { token, hash } = generateToken()
  assert.match(token, /^mv_[A-Za-z0-9_-]{43}$/)
  assert.equal(hash, hashToken(token))
  assert.match(hash, /^[0-9a-f]{64}$/)
})

test('authenticate resolves a live token and rejects everything else', () => {
  const db = openDb(':memory:')
  const u = createUser(db, 'Miguel')
  const token = issueToken(db, u.id, 'MacBook')
  const ok = authenticate(db, `Bearer ${token}`)
  assert.equal(ok.ok, true)
  if (ok.ok) assert.equal(ok.user.name, 'Miguel')

  assert.deepEqual(authenticate(db, undefined), { ok: false, error: 'missing_token' })
  assert.deepEqual(authenticate(db, 'Bearer mv_nope'), { ok: false, error: 'invalid_token' })
  assert.deepEqual(authenticate(db, 'Basic abc'), { ok: false, error: 'missing_token' })

  const row = db.select().from(deviceTokens).where(eq(deviceTokens.userId, u.id)).get()!
  revokeToken(db, row.id)
  assert.deepEqual(authenticate(db, `Bearer ${token}`), { ok: false, error: 'token_revoked' })

  const t2 = issueToken(db, u.id, 'second')
  db.update(users).set({ disabledAt: 1 }).where(eq(users.id, u.id)).run()
  assert.deepEqual(authenticate(db, `Bearer ${t2}`), { ok: false, error: 'user_disabled' })
})

test('last_used_at is written at most once per 5 minutes', () => {
  const db = openDb(':memory:')
  const u = createUser(db, 'A')
  const token = issueToken(db, u.id, 'x')
  authenticate(db, `Bearer ${token}`, 1_000_000)
  authenticate(db, `Bearer ${token}`, 1_000_000 + 60_000)
  const row = db.select().from(deviceTokens).where(eq(deviceTokens.userId, u.id)).get()!
  assert.equal(row.lastUsedAt, 1_000_000)
  authenticate(db, `Bearer ${token}`, 1_000_000 + 6 * 60_000)
  const row2 = db.select().from(deviceTokens).where(eq(deviceTokens.userId, u.id)).get()!
  assert.equal(row2.lastUsedAt, 1_000_000 + 6 * 60_000)
})
```

- [ ] **Step 2: Run to verify failure**

Run: `cd server && npm test`
Expected: FAIL — cannot find `../src/auth.js`.

- [ ] **Step 3: Write `server/src/auth.ts`**

```ts
import { createHash, randomBytes } from 'node:crypto'
import { eq } from 'drizzle-orm'
import { nanoid } from 'nanoid'
import type { Db } from './db/client.js'
import { deviceTokens, users, now } from './db/schema.js'

export type AuthUser = { id: string; name: string }
export type AuthResult =
  | { ok: true; user: AuthUser; tokenId: string }
  | { ok: false; error: 'missing_token' | 'invalid_token' | 'token_revoked' | 'user_disabled' }

const LAST_USED_THROTTLE_MS = 5 * 60_000

export function hashToken(token: string): string {
  return createHash('sha256').update(token).digest('hex')
}

/** `mv_` + 32 random bytes base64url. Shown once; only the hash is stored. */
export function generateToken(): { token: string; hash: string } {
  const token = `mv_${randomBytes(32).toString('base64url')}`
  return { token, hash: hashToken(token) }
}

export function createUser(db: Db, name: string): AuthUser {
  const id = nanoid()
  db.insert(users).values({ id, name, createdAt: now() }).run()
  return { id, name }
}

export function issueToken(db: Db, userId: string, label: string): string {
  const { token, hash } = generateToken()
  db.insert(deviceTokens).values({ id: nanoid(), userId, tokenHash: hash, label, createdAt: now() }).run()
  return token
}

export function revokeToken(db: Db, tokenId: string): void {
  db.update(deviceTokens).set({ revokedAt: now() }).where(eq(deviceTokens.id, tokenId)).run()
}

export function authenticate(db: Db, header: string | undefined, nowMs: number = now()): AuthResult {
  const m = header?.match(/^Bearer\s+(mv_[A-Za-z0-9_-]+)$/)
  if (!m) return { ok: false, error: 'missing_token' }
  const hash = hashToken(m[1]!)
  const row = db
    .select({
      tokenId: deviceTokens.id, revokedAt: deviceTokens.revokedAt, lastUsedAt: deviceTokens.lastUsedAt,
      userId: users.id, name: users.name, disabledAt: users.disabledAt,
    })
    .from(deviceTokens)
    .innerJoin(users, eq(users.id, deviceTokens.userId))
    .where(eq(deviceTokens.tokenHash, hash))
    .get()
  if (!row) return { ok: false, error: 'invalid_token' }
  if (row.revokedAt) return { ok: false, error: 'token_revoked' }
  if (row.disabledAt) return { ok: false, error: 'user_disabled' }
  if (!row.lastUsedAt || nowMs - row.lastUsedAt >= LAST_USED_THROTTLE_MS) {
    db.update(deviceTokens).set({ lastUsedAt: nowMs }).where(eq(deviceTokens.id, row.tokenId)).run()
  }
  return { ok: true, user: { id: row.userId, name: row.name }, tokenId: row.tokenId }
}
```

- [ ] **Step 4: Run the auth tests**

Run: `cd server && npm test`
Expected: all passing.

- [ ] **Step 5: Register the hook and rate limit in `app.ts`**

Add to `server/src/app.ts` (after helmet):
```ts
import rateLimit from '@fastify/rate-limit'
import { authenticate, hashToken, type AuthUser } from './auth.js'

declare module 'fastify' {
  interface FastifyRequest { user: AuthUser; tokenId: string }
}

// ...inside buildApp, after helmet:
app.decorateRequest('user', null)
app.decorateRequest('tokenId', '')
app.register(rateLimit, {
  max: 120,
  timeWindow: '1 minute',
  // Key by the token hash, never the raw bearer; unauthenticated callers share a per-IP bucket.
  keyGenerator: (req) => {
    const m = req.headers.authorization?.match(/^Bearer\s+(mv_[A-Za-z0-9_-]+)$/)
    return m ? hashToken(m[1]!) : `ip:${req.ip}`
  },
})
app.addHook('onRequest', async (req, reply) => {
  if (req.url === '/health') return
  const result = authenticate(deps.db, req.headers.authorization)
  if (!result.ok) {
    return reply.code(401).send({ error: result.error, message: 'Authentication failed' })
  }
  req.user = result.user
  req.tokenId = result.tokenId
})
```

Extend `test/health.test.ts` with:
```ts
test('other routes are 401 without a token', async () => {
  const app = buildApp({ db: openDb(':memory:'), llm: null as never, version: 'test' })
  const res = await app.inject({ method: 'GET', url: '/v1/me' })
  assert.equal(res.statusCode, 401)
  assert.equal(res.json().error, 'missing_token')
  await app.close()
})
```
(`/v1/me` does not exist yet; the hook answers 401 before the 404, which is the point.)

- [ ] **Step 6: Write the users CLI**

`server/src/cli/users.ts`:
```ts
import { eq } from 'drizzle-orm'
import { openDb } from '../db/client.js'
import { env } from '../env.js'
import { createUser, issueToken, revokeToken } from '../auth.js'
import { deviceTokens, users } from '../db/schema.js'

const [cmd, ...rest] = process.argv.slice(2)
const db = openDb(env.databasePath())

function usage(): never {
  console.error('usage: users add <name> [--label <label>] | users list | users token <userId> [--label <label>] | users revoke <tokenId>')
  process.exit(2)
}

function flag(name: string, fallback: string): string {
  const i = rest.indexOf(`--${name}`)
  return i >= 0 && rest[i + 1] ? rest[i + 1]! : fallback
}

switch (cmd) {
  case 'add': {
    const name = rest[0]
    if (!name || name.startsWith('--')) usage()
    const u = createUser(db, name)
    const token = issueToken(db, u.id, flag('label', 'default'))
    console.log(`user ${u.id} (${u.name})`)
    console.log(`token (shown once): ${token}`)
    break
  }
  case 'token': {
    const userId = rest[0]
    if (!userId) usage()
    const token = issueToken(db, userId, flag('label', 'default'))
    console.log(`token (shown once): ${token}`)
    break
  }
  case 'list': {
    const rows = db
      .select({ userId: users.id, name: users.name, disabledAt: users.disabledAt, tokenId: deviceTokens.id, label: deviceTokens.label, lastUsedAt: deviceTokens.lastUsedAt, revokedAt: deviceTokens.revokedAt })
      .from(users)
      .leftJoin(deviceTokens, eq(deviceTokens.userId, users.id))
      .all()
    for (const r of rows) {
      const state = r.revokedAt ? 'revoked' : r.disabledAt ? 'disabled' : 'active'
      console.log(`${r.userId}\t${r.name}\t${r.tokenId ?? '-'}\t${r.label ?? '-'}\t${state}\tlast_used=${r.lastUsedAt ? new Date(r.lastUsedAt).toISOString() : 'never'}`)
    }
    break
  }
  case 'revoke': {
    const tokenId = rest[0]
    if (!tokenId) usage()
    revokeToken(db, tokenId)
    console.log(`revoked ${tokenId}`)
    break
  }
  default:
    usage()
}
db.raw.close()
```

- [ ] **Step 7: Run everything, try the CLI**

Run: `cd server && npm test && npm run typecheck && DATABASE_PATH=./data/dev.db npm run users -- add "Miguel" --label MacBook && DATABASE_PATH=./data/dev.db npm run users -- list`
Expected: tests pass; a `mv_…` token printed once; the list shows Miguel active.

- [ ] **Step 8: Commit**

```bash
git add server/src/auth.ts server/src/cli/users.ts server/src/app.ts server/test/auth.test.ts server/test/health.test.ts
git commit -m "feat(server): device tokens, users CLI and bearer auth hook"
```

---

### Task 4: LLM client, error classification and the concurrency semaphore

**Files:**
- Create: `server/src/llm/client.ts`, `server/src/llm/errors.ts`, `server/src/llm/semaphore.ts`
- Test: `server/test/llm-errors.test.ts`, `server/test/semaphore.test.ts`

**Interfaces:**
- Produces: `type LlmRequest = { system: string; user: string; model: string; reasoning: string; maxTokens: number; timeoutMs: number }`, `type LlmResponse = { content: string; finishReason: string | null; completionTokens: number | null; reasoningChars: number }`, `type LlmCaller = (req: LlmRequest) => Promise<LlmResponse>`, `makeOllamaCaller(): LlmCaller`, `probeModels(): Promise<string[]>`, `classifyLlmError(err): LlmErrorInfo` (kinds: `auth | billing | entitlement | model-not-found | rate-limit | timeout | network | server | config | unknown`), `class Semaphore { constructor(max: number); tryAcquire(): (() => void) | null; get active(): number }`.

- [ ] **Step 1: Write the failing tests**

`server/test/semaphore.test.ts`:
```ts
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { Semaphore } from '../src/llm/semaphore.js'

test('fails fast at capacity and releases exactly once', () => {
  const s = new Semaphore(2)
  const a = s.tryAcquire(); const b = s.tryAcquire()
  assert.ok(a && b)
  assert.equal(s.tryAcquire(), null)
  a!(); a!() // double release is a no-op
  assert.equal(s.active, 1)
  assert.ok(s.tryAcquire())
  assert.equal(s.tryAcquire(), null)
  b!()
  assert.equal(s.active, 1)
})
```

`server/test/llm-errors.test.ts`:
```ts
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { classifyLlmError } from '../src/llm/errors.js'

test('reads Ollama string bodies and OpenAI object bodies', () => {
  assert.equal(classifyLlmError({ status: 401, error: 'invalid api key' }).kind, 'auth')
  assert.equal(classifyLlmError({ status: 402, error: 'extra usage balance empty' }).kind, 'billing')
  assert.equal(classifyLlmError({ status: 403, error: 'payment past due' }).kind, 'billing')
  assert.equal(classifyLlmError({ status: 403, error: 'model not in plan' }).kind, 'entitlement')
  assert.equal(classifyLlmError({ status: 404, error: { message: 'model x not found' } }).kind, 'model-not-found')
  assert.equal(classifyLlmError({ status: 429, error: 'slow down' }).kind, 'rate-limit')
  assert.equal(classifyLlmError({ status: 503, error: 'busy' }).kind, 'server')
  assert.equal(classifyLlmError({ name: 'APIConnectionTimeoutError', message: 'timed out' }).kind, 'timeout')
  assert.equal(classifyLlmError({ name: 'APIConnectionError', message: 'ECONNRESET' }).kind, 'network')
  assert.equal(classifyLlmError(new Error('Missing required environment variable: LLM_API_KEY')).kind, 'config')
  const d = classifyLlmError({ status: 500, error: 'x'.repeat(500) })
  assert.equal(d.detail.length, 240)
})
```

- [ ] **Step 2: Run to verify failure**

Run: `cd server && npm test`
Expected: FAIL — modules not found.

- [ ] **Step 3: Write `semaphore.ts`**

```ts
/** Fail-fast counting semaphore: Ollama Cloud queues over-cap requests and we would rather paste raw than wait. */
export class Semaphore {
  #active = 0
  constructor(private readonly max: number) {}
  get active(): number { return this.#active }
  tryAcquire(): (() => void) | null {
    if (this.#active >= this.max) return null
    this.#active++
    let released = false
    return () => {
      if (released) return
      released = true
      this.#active--
    }
  }
}
```

- [ ] **Step 4: Write `errors.ts`** (copied from `hub/miraside/lib/llm/errors.ts`, provider switch removed)

```ts
// Turning a provider failure into something a caller can act on.
//
// ── The trap ────────────────────────────────────────────────────────────────
// The OpenAI SDK does `const error = errorResponse?.['error']` when building an
// APIError. Ollama Cloud's body is `{"error": "<string>"}`, so `err.error` is a
// plain STRING, not the `{ message }` object the OpenAI API returns. A
// classifier that reads `err.error.message` finds `undefined` and silently
// falls through to 'unknown' for every Ollama failure.

export type LlmErrorKind =
  | 'auth' | 'billing' | 'entitlement' | 'model-not-found' | 'rate-limit'
  | 'timeout' | 'network' | 'server' | 'config' | 'unknown'

export interface LlmErrorInfo {
  kind: LlmErrorKind
  status?: number
  /** Short, safe to log. Never contains prompt or transcript content. */
  detail: string
}

function bodyText(err: unknown): string {
  const e = err as { error?: unknown; message?: unknown } | null
  const body = e?.error
  if (typeof body === 'string') return body
  if (body && typeof body === 'object') {
    const m = (body as { message?: unknown }).message
    if (typeof m === 'string') return m
  }
  return typeof e?.message === 'string' ? e.message : String(err)
}

export function classifyLlmError(err: unknown): LlmErrorInfo {
  const status = (err as { status?: number } | null)?.status
  const name = (err as { name?: string } | null)?.name
  const body = bodyText(err)
  const detail = body.slice(0, 240)

  if (name === 'APIConnectionTimeoutError') return { kind: 'timeout', detail }
  if (name === 'APIConnectionError') return { kind: 'network', detail }
  if (status === 401) return { kind: 'auth', status, detail }
  // Ollama returns 402 for a model billed against a separate "extra usage" balance when it is empty.
  if (status === 402) return { kind: 'billing', status, detail }
  if (status === 403) {
    return { kind: /past due|payment|billing/i.test(body) ? 'billing' : 'entitlement', status, detail }
  }
  if (status === 404) return { kind: 'model-not-found', status, detail }
  if (status === 429) return { kind: 'rate-limit', status, detail }
  if (typeof status === 'number' && status >= 500) return { kind: 'server', status, detail }
  if (/Missing required environment variable/i.test(body)) return { kind: 'config', detail }
  return { kind: 'unknown', status, detail }
}
```

- [ ] **Step 5: Write `client.ts`**

```ts
import OpenAI from 'openai'
import { env } from '../env.js'

export interface LlmRequest {
  system: string
  user: string
  model: string
  reasoning: string // none | low | medium | high
  maxTokens: number
  timeoutMs: number
}
export interface LlmResponse {
  content: string
  finishReason: string | null
  completionTokens: number | null
  /** Ollama returns thinking in `message.reasoning`; we only measure it. */
  reasoningChars: number
}
export type LlmCaller = (req: LlmRequest) => Promise<LlmResponse>

let cached: OpenAI | null = null
/** Cached singleton (pattern from hub/tools/ContentAgent/lib/agent/llm.ts). No SDK retries: the Mac owns the budget. */
export function ollamaClient(): OpenAI {
  cached ??= new OpenAI({ baseURL: env.llmBaseUrl(), apiKey: env.llmApiKey(), maxRetries: 0 })
  return cached
}

export function makeOllamaCaller(): LlmCaller {
  return async (req) => {
    const body: Record<string, unknown> = {
      model: req.model,
      messages: [{ role: 'system', content: req.system }, { role: 'user', content: req.user }],
      temperature: 0.1,
      max_tokens: req.maxTokens,
      stream: false,
    }
    if (req.reasoning && req.reasoning !== 'default') body.reasoning_effort = req.reasoning
    const res = await ollamaClient().chat.completions.create(body as never, { timeout: req.timeoutMs })
    const choice = res.choices?.[0]
    const msg = (choice?.message ?? {}) as { content?: string | null; reasoning?: string | null }
    return {
      content: msg.content ?? '',
      finishReason: choice?.finish_reason ?? null,
      completionTokens: res.usage?.completion_tokens ?? null,
      reasoningChars: (msg.reasoning ?? '').length,
    }
  }
}

/** Health probe: a models list, never a completion (a completion would occupy a concurrency slot). */
export async function probeModels(): Promise<string[]> {
  const list = await ollamaClient().models.list({ timeout: 5000 } as never)
  return list.data.map((m) => m.id)
}
```

- [ ] **Step 6: Run tests and typecheck**

Run: `cd server && npm test && npm run typecheck`
Expected: all passing. If `chat.completions.create` rejects `reasoning_effort: 'none'` at the type level, the `as never` cast on the body is what keeps it compiling — Ollama accepts the value at runtime (docs/SPIKES.md).

- [ ] **Step 7: Commit**

```bash
git add server/src/llm server/test/semaphore.test.ts server/test/llm-errors.test.ts
git commit -m "feat(server): Ollama client, error classifier and fail-fast semaphore"
```

---

### Task 5: Refine core — prompt, output guards, noise detection

**Files:**
- Create: `server/src/llm/prompt.ts`, `server/src/llm/guards.ts`, `server/src/llm/refine.ts`, `server/src/llm/fillers.ts`
- Test: `server/test/guards.test.ts`, `server/test/refine.test.ts`

**Interfaces:**
- Produces: `buildSystemPrompt(dictionary: { term: string; replacement: string | null }[]): string`, `isNoise(raw: string): boolean`, `normalizeOutput(s: string): string`, `checkGuards(raw: string, out: string): { ok: true } | { ok: false; reason: 'empty' | 'too-short' | 'too-long' | 'preamble' | 'think-leak' }`, `estimateTokens(s: string): number`, `refineText(input: RefineInput, llm: LlmCaller): Promise<RefineOutcome>` where `RefineInput = { raw: string; dictionary: {term, replacement}[]; model: string; reasoning: string; timeoutMs: number }` and `RefineOutcome = { cleaned: string; fallbackReason: FallbackReason | null; llmMs: number; model: string; reasoningChars: number; completionTokens: number | null; errorKind?: string }`.

- [ ] **Step 1: Write the failing guard tests**

`server/test/guards.test.ts`:
```ts
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { isNoise, normalizeOutput, checkGuards, estimateTokens } from '../src/llm/guards.js'

test('noise is fewer than 4 words, all fillers', () => {
  assert.equal(isNoise('hã hã'), true)
  assert.equal(isNoise('um uh hmm'), true)
  assert.equal(isNoise('sim'), false)
  assert.equal(isNoise('hã então olá'), false)
  assert.equal(isNoise(''), true)
})

test('normalizeOutput strips fences, quotes, preambles and think blocks', () => {
  assert.equal(normalizeOutput('```\nOlá.\n```'), 'Olá.')
  assert.equal(normalizeOutput('"Olá."'), 'Olá.')
  assert.equal(normalizeOutput('Here is the cleaned text:\nOlá.'), 'Olá.')
  assert.equal(normalizeOutput('<think>reasoning</think>Olá.'), 'Olá.')
  assert.equal(normalizeOutput('some reasoning</think>Olá.'), 'Olá.')
  assert.equal(normalizeOutput('  Olá.  \n'), 'Olá.')
})

test('checkGuards: short inputs skip the lower bound; long ones enforce it', () => {
  assert.deepEqual(checkGuards('Hã hã então olá', 'Olá.'), { ok: true })
  const raw = 'hã então eu acho que que amanhã vamos vamos fechar o contrato tipo às três da tarde'
  assert.deepEqual(checkGuards(raw, 'Eu acho que amanhã vamos fechar o contrato às três da tarde.'), { ok: true })
  assert.deepEqual(checkGuards(raw, 'Ok.'), { ok: false, reason: 'too-short' })
  assert.deepEqual(checkGuards(raw, raw.repeat(4)), { ok: false, reason: 'too-long' })
  assert.deepEqual(checkGuards(raw, ''), { ok: false, reason: 'empty' })
  assert.deepEqual(checkGuards(raw, 'Cleaned text: Eu acho que amanhã vamos fechar o contrato às três da tarde.'), { ok: false, reason: 'preamble' })
  assert.deepEqual(checkGuards(raw, '<think>still here Eu acho que amanhã vamos fechar o contrato às três da tarde.'), { ok: false, reason: 'think-leak' })
})

test('estimateTokens is ~chars/3.5 with a floor of 1', () => {
  assert.equal(estimateTokens(''), 1)
  assert.equal(estimateTokens('a'.repeat(35)), 10)
})
```

- [ ] **Step 2: Run to verify failure**

Run: `cd server && npm test`
Expected: FAIL — module not found.

- [ ] **Step 3: Write `fillers.ts` and `guards.ts`**

`server/src/llm/fillers.ts`:
```ts
/** Tokens that carry no content in pt-PT or en dictation. Lower-case, no punctuation. */
export const FILLERS = new Set([
  'um', 'uh', 'uhm', 'umm', 'hmm', 'hm', 'mm', 'ah', 'eh', 'er', 'erm',
  'hã', 'hum', 'ééé', 'éé', 'ahn', 'ãh', 'hmmm',
])
```

`server/src/llm/guards.ts`:
```ts
import { FILLERS } from './fillers.js'

const words = (s: string): string[] => s.toLowerCase().replace(/[^\p{L}\p{N}\s]/gu, ' ').split(/\s+/).filter(Boolean)

/** Raw under 4 words where every word is a filler: the model is right to return nothing. */
export function isNoise(raw: string): boolean {
  const w = words(raw)
  return w.length < 4 && w.every((x) => FILLERS.has(x))
}

const PREAMBLE = /^(here('s| is)( the| your)?( cleaned| corrected| revised)?( text| version| transcript)?\s*:?\s*\n?)/i

/** Strip what a rewrite model adds around the answer. Pure; safe to call twice. */
export function normalizeOutput(s: string): string {
  let out = s.trim()
  // Full think block, or a stray closing tag left by a model whose parser was not engaged.
  out = out.replace(/<think>[\s\S]*?<\/think>/g, '')
  const close = out.lastIndexOf('</think>')
  if (close >= 0) out = out.slice(close + '</think>'.length)
  out = out.trim()
  const fence = out.match(/^```[a-z]*\n([\s\S]*?)\n```$/)
  if (fence) out = fence[1]!.trim()
  if (out.length >= 2 && ((out.startsWith('"') && out.endsWith('"')) || (out.startsWith('“') && out.endsWith('”')))) {
    out = out.slice(1, -1).trim()
  }
  out = out.replace(PREAMBLE, '').trim()
  return out
}

export type GuardResult = { ok: true } | { ok: false; reason: 'empty' | 'too-short' | 'too-long' | 'preamble' | 'think-leak' }

/** Invariants for "a rewrite, not an answer". Ratios from docs/PLAN.md §4.3. */
export function checkGuards(raw: string, out: string): GuardResult {
  if (/<think>/i.test(out)) return { ok: false, reason: 'think-leak' }
  if (out.length === 0) return { ok: false, reason: 'empty' }
  if (/^\s*cleaned text\s*:/im.test(out)) return { ok: false, reason: 'preamble' }
  const rawLen = raw.trim().length
  if (out.length > 2.5 * rawLen + 20) return { ok: false, reason: 'too-long' }
  if (rawLen >= 25 && out.length < 0.3 * rawLen - 12) return { ok: false, reason: 'too-short' }
  return { ok: true }
}

export function estimateTokens(s: string): number {
  return Math.max(1, Math.round(s.length / 3.5))
}
```

- [ ] **Step 4: Run the guard tests**

Run: `cd server && npm test`
Expected: guard tests pass.

- [ ] **Step 5: Write the failing refine tests**

`server/test/refine.test.ts`:
```ts
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { refineText } from '../src/llm/refine.js'
import { buildSystemPrompt } from '../src/llm/prompt.js'
import type { LlmCaller } from '../src/llm/client.js'

const raw = 'hã então eu acho que que amanhã vamos vamos fechar o contrato tipo às três da tarde'
const base = { raw, dictionary: [], model: 'gemma4', reasoning: 'none', timeoutMs: 3000 }
const ok = (content: string, finish = 'stop'): LlmCaller => async () => ({ content, finishReason: finish, completionTokens: 20, reasoningChars: 0 })

test('happy path returns the normalized cleaned text', async () => {
  const r = await refineText(base, ok('"Eu acho que amanhã vamos fechar o contrato às três da tarde."'))
  assert.equal(r.cleaned, 'Eu acho que amanhã vamos fechar o contrato às três da tarde.')
  assert.equal(r.fallbackReason, null)
  assert.equal(r.model, 'gemma4')
})

test('guard rejection falls back to raw', async () => {
  const r = await refineText(base, ok('Ok.'))
  assert.equal(r.cleaned, raw)
  assert.equal(r.fallbackReason, 'guard-rejected')
})

test('truncation, timeout and errors map to their reasons', async () => {
  assert.equal((await refineText(base, ok('Eu acho que amanhã vamos fechar', 'length'))).fallbackReason, 'llm-truncated')
  const timeout: LlmCaller = async () => { throw Object.assign(new Error('t'), { name: 'APIConnectionTimeoutError' }) }
  assert.equal((await refineText(base, timeout)).fallbackReason, 'llm-timeout')
  const boom: LlmCaller = async () => { throw Object.assign(new Error('x'), { status: 503, error: 'busy' }) }
  const r = await refineText(base, boom)
  assert.equal(r.fallbackReason, 'llm-error')
  assert.equal(r.errorKind, 'server')
  assert.equal(r.cleaned, raw)
})

test('max_tokens floors at 512 and scales with input', async () => {
  let seen = 0
  const spy: LlmCaller = async (req) => { seen = req.maxTokens; return { content: 'x'.repeat(30), finishReason: 'stop', completionTokens: 1, reasoningChars: 0 } }
  await refineText({ ...base, raw: 'sim' }, spy)
  assert.equal(seen, 512)
  await refineText({ ...base, raw: 'a'.repeat(3500) }, spy)
  assert.equal(seen, 1000 * 3 + 192)
})

test('system prompt embeds the dictionary', () => {
  const p = buildSystemPrompt([{ term: 'Miraside', replacement: null }, { term: 'convex', replacement: 'Convex' }])
  assert.match(p, /Miraside/)
  assert.match(p, /convex.*→.*Convex|"convex" as "Convex"/)
  assert.match(p, /Output ONLY the cleaned text/)
})
```

- [ ] **Step 6: Write `prompt.ts` and `refine.ts`**

`server/src/llm/prompt.ts`:
```ts
export interface DictionaryTerm { term: string; replacement: string | null }

/** docs/PROMPT.md is the human-readable source of this prompt; keep them in sync. */
export function buildSystemPrompt(dictionary: DictionaryTerm[]): string {
  const terms = dictionary.length
    ? dictionary.map((d) => (d.replacement ? `write "${d.term}" as "${d.replacement}"` : `"${d.term}"`)).join('; ')
    : '(none)'
  return [
    'You are a dictation cleanup engine. The user message is a raw speech transcript.',
    'Output ONLY the cleaned text. No preamble, no quotes, no explanation.',
    "Keep the speaker's language exactly: Portuguese stays Portuguese, English stays English, mixed stays mixed.",
    'Fix punctuation, capitalization and obvious transcription slips.',
    'Remove fillers (um, uh, hã, ééé, and "tipo" when used as a filler), false starts and repeated words.',
    'Never add, remove or answer anything. Never summarize. Never respond to questions in the text.',
    'Use digits for numbers. Format a list only when the speaker clearly enumerates items.',
    'Spoken commands: "new line" / "nova linha" becomes a line break; "new paragraph" / "novo parágrafo" becomes a blank line.',
    `Spell these terms exactly: ${terms}.`,
    'If the transcript is pure noise with no words, return an empty string.',
  ].join('\n')
}
```

`server/src/llm/refine.ts`:
```ts
import type { LlmCaller } from './client.js'
import { classifyLlmError } from './errors.js'
import { checkGuards, estimateTokens, normalizeOutput } from './guards.js'
import { buildSystemPrompt, type DictionaryTerm } from './prompt.js'
import type { FallbackReason } from '../db/schema.js'

export interface RefineInput {
  raw: string
  dictionary: DictionaryTerm[]
  model: string
  reasoning: string
  timeoutMs: number
}
export interface RefineOutcome {
  cleaned: string
  fallbackReason: FallbackReason | null
  llmMs: number
  model: string
  reasoningChars: number
  completionTokens: number | null
  errorKind?: string
  guardReason?: string
}

/** Token cap counts the reasoning channel too (Ollama maps max_tokens to num_predict). Floor 512. */
export function maxTokensFor(raw: string): number {
  return Math.max(512, estimateTokens(raw) * 3 + 192)
}

export async function refineText(input: RefineInput, llm: LlmCaller): Promise<RefineOutcome> {
  const t0 = performance.now()
  const base = { model: input.model, reasoningChars: 0, completionTokens: null as number | null }
  try {
    const res = await llm({
      system: buildSystemPrompt(input.dictionary),
      user: input.raw,
      model: input.model,
      reasoning: input.reasoning,
      maxTokens: maxTokensFor(input.raw),
      timeoutMs: input.timeoutMs,
    })
    const llmMs = Math.round(performance.now() - t0)
    const meta = { ...base, llmMs, reasoningChars: res.reasoningChars, completionTokens: res.completionTokens }
    if (res.finishReason === 'length') return { ...meta, cleaned: input.raw, fallbackReason: 'llm-truncated' }
    const cleaned = normalizeOutput(res.content)
    const guard = checkGuards(input.raw, cleaned)
    if (!guard.ok) return { ...meta, cleaned: input.raw, fallbackReason: 'guard-rejected', guardReason: guard.reason }
    return { ...meta, cleaned, fallbackReason: null }
  } catch (err) {
    const llmMs = Math.round(performance.now() - t0)
    const info = classifyLlmError(err)
    const fallbackReason: FallbackReason = info.kind === 'timeout' ? 'llm-timeout' : 'llm-error'
    return { ...base, llmMs, cleaned: input.raw, fallbackReason, errorKind: info.kind }
  }
}
```

- [ ] **Step 7: Run all tests and typecheck**

Run: `cd server && npm test && npm run typecheck`
Expected: all passing.

- [ ] **Step 8: Write `docs/PROMPT.md`** with the prompt text from `buildSystemPrompt` verbatim, the guard rules, and the spike table from `docs/SPIKES.md`; the bench in Task 8 appends its table.

- [ ] **Step 9: Commit**

```bash
git add server/src/llm docs/PROMPT.md server/test/guards.test.ts server/test/refine.test.ts
git commit -m "feat(server): refine pipeline with prompt, output guards and noise detection"
```

---

### Task 6: `/v1/refine`, `/v1/dictations` (upsert by clientId), PATCH by-client, history

**Files:**
- Create: `server/src/routes/refine.ts`, `server/src/routes/dictations.ts`, `server/src/routes/schemas.ts`, `server/src/dictations-repo.ts`
- Modify: `server/src/app.ts` (register routes; real `AppDeps`), `server/src/index.ts`
- Test: `server/test/routes-refine.test.ts`

**Interfaces:**
- Consumes: `authenticate`, `refineText`, `Semaphore`, `isNoise`, `Db` tables.
- Produces: `AppDeps = { db: Db; llm: LlmCaller; version: string; llmSemaphore?: Semaphore; probeLlm?: () => Promise<string[]> }`; `upsertDictation(db, row: DictationUpsert): { id: string }` and `setInjected(db, userId, clientId, injected): boolean`; the zod schemas `RefineBody`, `DictationBody`, `InjectedBody` in `schemas.ts`.

- [ ] **Step 1: Write the failing route tests**

`server/test/routes-refine.test.ts`:
```ts
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { buildApp } from '../src/app.js'
import { openDb } from '../src/db/client.js'
import { createUser, issueToken } from '../src/auth.js'
import { dictations } from '../src/db/schema.js'
import type { LlmCaller } from '../src/llm/client.js'
import { Semaphore } from '../src/llm/semaphore.js'

const raw = 'hã então eu acho que que amanhã vamos vamos fechar o contrato tipo às três da tarde'
const body = (over: Record<string, unknown> = {}) => ({
  clientId: '11111111-1111-4111-8111-111111111111', raw, mode: 'clean', languageSetting: 'auto', languageDetected: 'pt',
  budgetMs: 4000, context: { appBundleId: 'com.apple.mail', appName: 'Mail' }, timing: { audioMs: 6000, asrMs: 1400 },
  asrModel: 'large-v3-turbo-632MB', clientVersion: '0.1.0', createdAt: 1757400000000, ...over,
})

function setup(llm: LlmCaller, semaphore = new Semaphore(5)) {
  const db = openDb(':memory:')
  const u = createUser(db, 'Miguel')
  const token = issueToken(db, u.id, 'test')
  const app = buildApp({ db, llm, version: 'test', llmSemaphore: semaphore })
  const post = (url: string, payload: unknown, method: 'POST' | 'PATCH' = 'POST') =>
    app.inject({ method, url, payload, headers: { authorization: `Bearer ${token}` } })
  return { db, app, post, token, userId: u.id }
}
const good: LlmCaller = async () => ({ content: 'Eu acho que amanhã vamos fechar o contrato às três da tarde.', finishReason: 'stop', completionTokens: 20, reasoningChars: 0 })

test('refine returns cleaned text and stores one row; a replay upserts, not duplicates', async () => {
  const { db, app, post } = setup(good)
  const r1 = await post('/v1/refine', body())
  assert.equal(r1.statusCode, 200)
  assert.equal(r1.json().cleaned, 'Eu acho que amanhã vamos fechar o contrato às três da tarde.')
  assert.equal(r1.json().fallbackReason, null)
  const r2 = await post('/v1/dictations', { ...body(), injected: 'raw', fallbackReason: 'client-timeout' })
  assert.equal(r2.statusCode, 200)
  const rows = db.select().from(dictations).all()
  assert.equal(rows.length, 1)
  assert.equal(rows[0]!.cleaned, 'Eu acho que amanhã vamos fechar o contrato às três da tarde.') // replay never erases the cleanup
  assert.equal(rows[0]!.injected, 'raw')
  assert.equal(rows[0]!.fallbackReason, 'client-timeout')
  await app.close()
})

test('literal mode skips the LLM; noise returns empty and injected none', async () => {
  let calls = 0
  const { app, post, db } = setup(async () => { calls++; return { content: 'x', finishReason: 'stop', completionTokens: 1, reasoningChars: 0 } })
  const lit = await post('/v1/refine', body({ mode: 'literal', clientId: 'a' }))
  assert.equal(lit.json().cleaned, raw)
  const noise = await post('/v1/refine', body({ raw: 'hã hã', clientId: 'b' }))
  assert.equal(noise.json().cleaned, '')
  assert.equal(calls, 0)
  const rows = db.select().from(dictations).all()
  assert.equal(rows.find((r) => r.clientId === 'b')?.injected, 'none')
  await app.close()
})

test('busy semaphore fails fast with llm-busy and raw text', async () => {
  const sem = new Semaphore(1)
  const release = sem.tryAcquire()!
  const { app, post } = setup(good, sem)
  const r = await post('/v1/refine', body())
  assert.equal(r.json().cleaned, raw)
  assert.equal(r.json().fallbackReason, 'llm-busy')
  release()
  await app.close()
})

test('server timeout is budgetMs - 700 capped by LLM_MAX_TIMEOUT_MS', async () => {
  let seen = 0
  const { app, post } = setup(async (req) => { seen = req.timeoutMs; return { content: 'Eu acho que amanhã vamos fechar o contrato às três da tarde.', finishReason: 'stop', completionTokens: 1, reasoningChars: 0 } })
  await post('/v1/refine', body({ budgetMs: 4000 }))
  assert.equal(seen, 3300)
  await post('/v1/refine', body({ budgetMs: 60000, clientId: 'c' }))
  assert.equal(seen, 14000)
  await app.close()
})

test('PATCH by-client sets injected; history lists newest first', async () => {
  const { app, post, token } = setup(good)
  await post('/v1/refine', body())
  const p = await post('/v1/dictations/by-client/11111111-1111-4111-8111-111111111111', { injected: 'cleaned' }, 'PATCH')
  assert.equal(p.statusCode, 200)
  const missing = await post('/v1/dictations/by-client/nope', { injected: 'cleaned' }, 'PATCH')
  assert.equal(missing.statusCode, 404)
  const list = await app.inject({ method: 'GET', url: '/v1/dictations?limit=10', headers: { authorization: `Bearer ${token}` } })
  assert.equal(list.json().items.length, 1)
  assert.equal(list.json().items[0].injected, 'cleaned')
  await app.close()
})

test('validation: raw over 64KB is rejected, unknown mode is 400', async () => {
  const { app, post } = setup(good)
  const bad = await post('/v1/refine', body({ mode: 'email' }))
  assert.equal(bad.statusCode, 400)
  const huge = await post('/v1/refine', body({ raw: 'a'.repeat(70_000) }))
  assert.equal(huge.statusCode, 413)
  await app.close()
})
```

- [ ] **Step 2: Run to verify failure**

Run: `cd server && npm test`
Expected: FAIL — routes missing (404s and type errors).

- [ ] **Step 3: Write `schemas.ts`**

`server/src/routes/schemas.ts`:
```ts
import { z } from 'zod'
import { FALLBACK_REASONS, INJECTED } from '../db/schema.js'

export const Context = z.object({ appBundleId: z.string().max(200).optional(), appName: z.string().max(200).optional() }).default({})
export const Timing = z.object({ audioMs: z.number().int().nonnegative(), asrMs: z.number().int().nonnegative() })

export const RefineBody = z.object({
  clientId: z.string().min(1).max(64),
  raw: z.string().max(60_000),
  mode: z.enum(['clean', 'literal']),
  languageSetting: z.string().max(16).default('auto'),
  languageDetected: z.string().max(16).optional(),
  budgetMs: z.number().int().min(1000).max(120_000).default(4000),
  context: Context,
  timing: Timing,
  asrModel: z.string().max(80),
  clientVersion: z.string().max(40),
  createdAt: z.number().int().positive(),
})
export type RefineBody = z.infer<typeof RefineBody>

export const DictationBody = RefineBody.omit({ budgetMs: true }).extend({
  injected: z.enum(INJECTED),
  fallbackReason: z.enum(FALLBACK_REASONS).nullable().default(null),
})
export type DictationBody = z.infer<typeof DictationBody>

export const InjectedBody = z.object({ injected: z.enum(INJECTED) })

export const ErrorReply = z.object({ error: z.string(), message: z.string() })
```

- [ ] **Step 4: Write `dictations-repo.ts`**

```ts
import { and, desc, eq, lt, sql } from 'drizzle-orm'
import { nanoid } from 'nanoid'
import type { Db } from './db/client.js'
import { dictations, type FallbackReason, type Injected } from './db/schema.js'

export interface DictationUpsert {
  userId: string
  clientId: string
  createdAt: number
  raw: string
  cleaned: string | null
  injected: Injected | null
  mode: string
  languageSetting: string
  languageDetected: string | null
  appBundleId: string | null
  appName: string | null
  audioMs: number | null
  asrMs: number | null
  llmMs: number | null
  asrModel: string | null
  llmModel: string | null
  clientVersion: string | null
  fallbackReason: FallbackReason | null
}

/**
 * Insert or update on (user_id, client_id). A replay never erases a cleanup that already
 * happened (`cleaned` is only set when non-null) and keeps the first fallback reason.
 */
export function upsertDictation(db: Db, row: DictationUpsert): { id: string } {
  const id = nanoid()
  db.insert(dictations)
    .values({ id, ...row })
    .onConflictDoUpdate({
      target: [dictations.userId, dictations.clientId],
      set: {
        cleaned: sql`coalesce(excluded.cleaned, ${dictations.cleaned})`,
        injected: sql`coalesce(excluded.injected, ${dictations.injected})`,
        fallbackReason: sql`coalesce(${dictations.fallbackReason}, excluded.fallback_reason)`,
        llmMs: sql`coalesce(excluded.llm_ms, ${dictations.llmMs})`,
        llmModel: sql`coalesce(excluded.llm_model, ${dictations.llmModel})`,
        languageDetected: sql`coalesce(excluded.language_detected, ${dictations.languageDetected})`,
      },
    })
    .run()
  const got = db.select({ id: dictations.id }).from(dictations)
    .where(and(eq(dictations.userId, row.userId), eq(dictations.clientId, row.clientId))).get()!
  return { id: got.id }
}

export function setInjected(db: Db, userId: string, clientId: string, injected: Injected): boolean {
  const r = db.update(dictations).set({ injected })
    .where(and(eq(dictations.userId, userId), eq(dictations.clientId, clientId))).run()
  return r.changes > 0
}

export function listDictations(db: Db, userId: string, limit: number, before?: number) {
  const where = before ? and(eq(dictations.userId, userId), lt(dictations.createdAt, before)) : eq(dictations.userId, userId)
  const items = db.select().from(dictations).where(where).orderBy(desc(dictations.createdAt)).limit(limit).all()
  const nextBefore = items.length === limit ? items[items.length - 1]!.createdAt : null
  return { items, nextBefore }
}
```

- [ ] **Step 5: Write `routes/refine.ts`**

```ts
import type { FastifyInstance } from 'fastify'
import type { ZodTypeProvider } from 'fastify-type-provider-zod'
import { z } from 'zod'
import type { AppDeps } from '../app.js'
import { env } from '../env.js'
import { isNoise } from '../llm/guards.js'
import { refineText } from '../llm/refine.js'
import { loadDictionary } from './dictionary.js'
import { upsertDictation } from '../dictations-repo.js'
import { RefineBody, ErrorReply } from './schemas.js'
import { FALLBACK_REASONS } from '../db/schema.js'

const RefineReply = z.object({
  clientId: z.string(), cleaned: z.string(), raw: z.string(), model: z.string().nullable(), llmMs: z.number(),
  fallbackReason: z.enum(FALLBACK_REASONS).nullable(),
})

export function registerRefine(app: FastifyInstance, deps: AppDeps): void {
  app.withTypeProvider<ZodTypeProvider>().post('/v1/refine', {
    schema: { body: RefineBody, response: { 200: RefineReply, 401: ErrorReply } },
  }, async (req) => {
    const b = req.body
    const user = req.user
    const dictionary = loadDictionary(deps.db, user.id)
    const settings = deps.settingsFor?.(user.id)
    const model = settings?.llmModel ?? env.llmModel()
    const reasoning = env.llmReasoning()
    const timeoutMs = Math.min(b.budgetMs - 700, env.llmMaxTimeoutMs())

    let cleaned: string | null
    let fallbackReason: (typeof FALLBACK_REASONS)[number] | null = null
    let llmMs = 0
    let injected: 'none' | null = null
    let usedModel: string | null = null
    let logExtra: Record<string, unknown> = {}

    if (b.mode === 'literal') {
      cleaned = b.raw
    } else if (isNoise(b.raw)) {
      cleaned = ''
      injected = 'none'
    } else {
      const release = deps.llmSemaphore?.tryAcquire() ?? (() => {})
      if (deps.llmSemaphore && release === null) {
        cleaned = b.raw
        fallbackReason = 'llm-busy'
      } else {
        try {
          const out = await refineText({ raw: b.raw, dictionary, model, reasoning, timeoutMs }, deps.llm)
          cleaned = out.cleaned
          fallbackReason = out.fallbackReason
          llmMs = out.llmMs
          usedModel = out.model
          logExtra = { reasoningChars: out.reasoningChars, completionTokens: out.completionTokens, errorKind: out.errorKind, guardReason: out.guardReason }
        } finally {
          release()
        }
      }
    }

    upsertDictation(deps.db, {
      userId: user.id, clientId: b.clientId, createdAt: b.createdAt, raw: b.raw, cleaned, injected,
      mode: b.mode, languageSetting: b.languageSetting, languageDetected: b.languageDetected ?? null,
      appBundleId: b.context.appBundleId ?? null, appName: b.context.appName ?? null,
      audioMs: b.timing.audioMs, asrMs: b.timing.asrMs, llmMs: llmMs || null, asrModel: b.asrModel,
      llmModel: usedModel, clientVersion: b.clientVersion, fallbackReason,
    })
    req.log.info({ userId: user.id, rawChars: b.raw.length, cleanedChars: cleaned.length, llmMs, model: usedModel, fallbackReason, mode: b.mode, ...logExtra }, 'refine')
    if (env.logTranscripts()) req.log.debug({ raw: b.raw, cleaned }, 'refine transcript (dev only)')
    return { clientId: b.clientId, cleaned, raw: b.raw, model: usedModel, llmMs, fallbackReason }
  })
}
```

Note the semaphore branch: `tryAcquire()` returns `null` at capacity. Write it as:
```ts
const release = deps.llmSemaphore ? deps.llmSemaphore.tryAcquire() : () => {}
if (release === null) { cleaned = b.raw; fallbackReason = 'llm-busy' } else { try { … } finally { release() } }
```

- [ ] **Step 6: Write `routes/dictations.ts`**

```ts
import type { FastifyInstance } from 'fastify'
import type { ZodTypeProvider } from 'fastify-type-provider-zod'
import { z } from 'zod'
import type { AppDeps } from '../app.js'
import { listDictations, setInjected, upsertDictation } from '../dictations-repo.js'
import { DictationBody, InjectedBody, ErrorReply } from './schemas.js'

export function registerDictations(app: FastifyInstance, deps: AppDeps): void {
  const r = app.withTypeProvider<ZodTypeProvider>()

  // Outbox replay from the Mac: the refine call never got through, or the client timed out first.
  r.post('/v1/dictations', { schema: { body: DictationBody, response: { 200: z.object({ id: z.string() }) } } }, async (req) => {
    const b = req.body
    return upsertDictation(deps.db, {
      userId: req.user.id, clientId: b.clientId, createdAt: b.createdAt, raw: b.raw, cleaned: null, injected: b.injected,
      mode: b.mode, languageSetting: b.languageSetting, languageDetected: b.languageDetected ?? null,
      appBundleId: b.context.appBundleId ?? null, appName: b.context.appName ?? null,
      audioMs: b.timing.audioMs, asrMs: b.timing.asrMs, llmMs: null, asrModel: b.asrModel, llmModel: null,
      clientVersion: b.clientVersion, fallbackReason: b.fallbackReason,
    })
  })

  r.patch('/v1/dictations/by-client/:clientId', {
    schema: { params: z.object({ clientId: z.string().min(1).max(64) }), body: InjectedBody, response: { 200: z.object({ ok: z.literal(true) }), 404: ErrorReply } },
  }, async (req, reply) => {
    const ok = setInjected(deps.db, req.user.id, req.params.clientId, req.body.injected)
    if (!ok) return reply.code(404).send({ error: 'not_found', message: 'No dictation with that clientId' })
    return { ok: true as const }
  })

  r.get('/v1/dictations', {
    schema: { querystring: z.object({ limit: z.coerce.number().int().min(1).max(200).default(50), before: z.coerce.number().int().positive().optional() }) },
  }, async (req) => listDictations(deps.db, req.user.id, req.query.limit, req.query.before))
}
```

- [ ] **Step 7: Update `app.ts` and `index.ts`**

`AppDeps` becomes:
```ts
export interface AppDeps {
  db: Db
  llm: LlmCaller
  version: string
  llmSemaphore?: Semaphore
  probeLlm?: () => Promise<string[]>
  settingsFor?: (userId: string) => { llmModel?: string } | null
}
```
Register `registerRefine(app, deps)` and `registerDictations(app, deps)` after the auth hook. `/health` reports `llm: 'ok' | 'degraded'` from a 60 s-cached `deps.probeLlm` (never throws, never changes the 200). `index.ts` builds: `buildApp({ db: openDb(env.databasePath()), llm: makeOllamaCaller(), version, llmSemaphore: new Semaphore(env.llmConcurrency()), probeLlm: probeModels })`. Body limit 64 KB yields 413 for the huge payload test (Fastify's default `FST_ERR_CTP_BODY_TOO_LARGE`).

`loadDictionary` is defined in Task 7; for this task create `routes/dictionary.ts` with only:
```ts
import { isNull, or, eq } from 'drizzle-orm'
import type { Db } from '../db/client.js'
import { dictionaryEntries } from '../db/schema.js'
export function loadDictionary(db: Db, userId: string) {
  return db.select({ term: dictionaryEntries.term, replacement: dictionaryEntries.replacement }).from(dictionaryEntries)
    .where(or(isNull(dictionaryEntries.userId), eq(dictionaryEntries.userId, userId))).all()
}
```

- [ ] **Step 8: Run all tests and typecheck**

Run: `cd server && npm test && npm run typecheck`
Expected: all passing.

- [ ] **Step 9: Commit**

```bash
git add server/src server/test/routes-refine.test.ts
git commit -m "feat(server): /v1/refine with budget, semaphore and guards; dictations upsert by clientId"
```

---

### Task 7: `/v1/me`, settings and dictionary routes

**Files:**
- Create: `server/src/routes/me.ts`, `server/src/routes/settings.ts`; extend `server/src/routes/dictionary.ts`
- Modify: `server/src/app.ts` (register; wire `settingsFor`)
- Test: `server/test/routes-me.test.ts`

**Interfaces:**
- Produces: `Settings = { mode: 'clean' | 'literal'; language: 'auto' | 'pt' | 'en'; hotkey: 'fn' | 'rightOption' | 'rightCommand'; llmModel?: string }` with `DEFAULT_SETTINGS`, `getSettings(db, userId): Settings`, `putSettings(db, userId, s: Settings)`; dictionary `GET/POST/DELETE`.

- [ ] **Step 1: Write the failing tests**

`server/test/routes-me.test.ts`:
```ts
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { buildApp } from '../src/app.js'
import { openDb } from '../src/db/client.js'
import { createUser, issueToken } from '../src/auth.js'

function setup() {
  const db = openDb(':memory:')
  const u = createUser(db, 'Miguel')
  const token = issueToken(db, u.id, 't')
  const other = createUser(db, 'Rita')
  const otherToken = issueToken(db, other.id, 't')
  const app = buildApp({ db, llm: async () => ({ content: '', finishReason: 'stop', completionTokens: 0, reasoningChars: 0 }), version: 'test' })
  const call = (method: 'GET' | 'POST' | 'PUT' | 'DELETE', url: string, payload?: unknown, tok = token) =>
    app.inject({ method, url, payload, headers: { authorization: `Bearer ${tok}` } })
  return { app, call, otherToken }
}

test('/v1/me returns user, default settings, dictionary and server info', async () => {
  const { app, call } = setup()
  const r = await call('GET', '/v1/me')
  assert.equal(r.statusCode, 200)
  const j = r.json()
  assert.equal(j.user.name, 'Miguel')
  assert.deepEqual(j.settings, { mode: 'clean', language: 'auto', hotkey: 'fn' })
  assert.deepEqual(j.dictionary, [])
  assert.equal(j.server.version, 'test')
  assert.equal(typeof j.server.concurrency, 'number')
  await app.close()
})

test('settings round-trip and reject unknown values', async () => {
  const { app, call } = setup()
  const put = await call('PUT', '/v1/settings', { mode: 'literal', language: 'pt', hotkey: 'rightOption', llmModel: 'gpt-oss:120b' })
  assert.equal(put.statusCode, 200)
  const get = await call('GET', '/v1/settings')
  assert.deepEqual(get.json(), { mode: 'literal', language: 'pt', hotkey: 'rightOption', llmModel: 'gpt-oss:120b' })
  const bad = await call('PUT', '/v1/settings', { mode: 'email', language: 'auto', hotkey: 'fn' })
  assert.equal(bad.statusCode, 400)
  await app.close()
})

test('dictionary: team-wide entries are visible to everyone, personal ones only to their owner', async () => {
  const { app, call, otherToken } = setup()
  const a = await call('POST', '/v1/dictionary', { term: 'Miraside', teamWide: true })
  assert.equal(a.statusCode, 200)
  const b = await call('POST', '/v1/dictionary', { term: 'convex', replacement: 'Convex' })
  assert.equal(b.statusCode, 200)
  const mine = await call('GET', '/v1/dictionary')
  assert.equal(mine.json().length, 2)
  const theirs = await call('GET', '/v1/dictionary', undefined, otherToken)
  assert.equal(theirs.json().length, 1)
  const del = await call('DELETE', `/v1/dictionary/${b.json().id}`, undefined, otherToken)
  assert.equal(del.statusCode, 404) // not theirs
  const del2 = await call('DELETE', `/v1/dictionary/${b.json().id}`)
  assert.equal(del2.statusCode, 200)
  await app.close()
})
```

- [ ] **Step 2: Run to verify failure**

Run: `cd server && npm test`
Expected: FAIL — 404s.

- [ ] **Step 3: Write `routes/settings.ts`**

```ts
import type { FastifyInstance } from 'fastify'
import type { ZodTypeProvider } from 'fastify-type-provider-zod'
import { eq } from 'drizzle-orm'
import { z } from 'zod'
import type { AppDeps } from '../app.js'
import type { Db } from '../db/client.js'
import { userSettings, now } from '../db/schema.js'

export const SettingsSchema = z.object({
  mode: z.enum(['clean', 'literal']),
  language: z.enum(['auto', 'pt', 'en']),
  hotkey: z.enum(['fn', 'rightOption', 'rightCommand']),
  llmModel: z.string().min(1).max(80).optional(),
})
export type Settings = z.infer<typeof SettingsSchema>
export const DEFAULT_SETTINGS: Settings = { mode: 'clean', language: 'auto', hotkey: 'fn' }

export function getSettings(db: Db, userId: string): Settings {
  const row = db.select().from(userSettings).where(eq(userSettings.userId, userId)).get()
  if (!row) return DEFAULT_SETTINGS
  const parsed = SettingsSchema.safeParse(JSON.parse(row.json))
  return parsed.success ? parsed.data : DEFAULT_SETTINGS
}

export function putSettings(db: Db, userId: string, s: Settings): void {
  db.insert(userSettings).values({ userId, json: JSON.stringify(s), updatedAt: now() })
    .onConflictDoUpdate({ target: userSettings.userId, set: { json: JSON.stringify(s), updatedAt: now() } }).run()
}

export function registerSettings(app: FastifyInstance, deps: AppDeps): void {
  const r = app.withTypeProvider<ZodTypeProvider>()
  r.get('/v1/settings', { schema: { response: { 200: SettingsSchema } } }, async (req) => getSettings(deps.db, req.user.id))
  r.put('/v1/settings', { schema: { body: SettingsSchema, response: { 200: SettingsSchema } } }, async (req) => {
    putSettings(deps.db, req.user.id, req.body)
    return req.body
  })
}
```

- [ ] **Step 4: Extend `routes/dictionary.ts`**

```ts
import type { FastifyInstance } from 'fastify'
import type { ZodTypeProvider } from 'fastify-type-provider-zod'
import { and, eq, isNull, or } from 'drizzle-orm'
import { nanoid } from 'nanoid'
import { z } from 'zod'
import type { AppDeps } from '../app.js'
import type { Db } from '../db/client.js'
import { dictionaryEntries, now } from '../db/schema.js'
import { ErrorReply } from './schemas.js'

export function loadDictionary(db: Db, userId: string) {
  return db.select({ term: dictionaryEntries.term, replacement: dictionaryEntries.replacement }).from(dictionaryEntries)
    .where(or(isNull(dictionaryEntries.userId), eq(dictionaryEntries.userId, userId))).all()
}

const Entry = z.object({ id: z.string(), term: z.string(), replacement: z.string().nullable(), note: z.string().nullable(), teamWide: z.boolean(), createdAt: z.number() })
const NewEntry = z.object({ term: z.string().min(1).max(80), replacement: z.string().min(1).max(80).optional(), note: z.string().max(200).optional(), teamWide: z.boolean().default(false) })

export function registerDictionary(app: FastifyInstance, deps: AppDeps): void {
  const r = app.withTypeProvider<ZodTypeProvider>()
  const toEntry = (row: typeof dictionaryEntries.$inferSelect) => ({ id: row.id, term: row.term, replacement: row.replacement, note: row.note, teamWide: row.userId === null, createdAt: row.createdAt })

  r.get('/v1/dictionary', { schema: { response: { 200: z.array(Entry) } } }, async (req) =>
    deps.db.select().from(dictionaryEntries).where(or(isNull(dictionaryEntries.userId), eq(dictionaryEntries.userId, req.user.id))).all().map(toEntry))

  r.post('/v1/dictionary', { schema: { body: NewEntry, response: { 200: Entry } } }, async (req) => {
    const row = { id: nanoid(), userId: req.body.teamWide ? null : req.user.id, term: req.body.term, replacement: req.body.replacement ?? null, note: req.body.note ?? null, createdAt: now() }
    deps.db.insert(dictionaryEntries).values(row).run()
    return toEntry(row)
  })

  r.delete('/v1/dictionary/:id', { schema: { params: z.object({ id: z.string() }), response: { 200: z.object({ ok: z.literal(true) }), 404: ErrorReply } } }, async (req, reply) => {
    const res = deps.db.delete(dictionaryEntries)
      .where(and(eq(dictionaryEntries.id, req.params.id), or(isNull(dictionaryEntries.userId), eq(dictionaryEntries.userId, req.user.id)))).run()
    if (res.changes === 0) return reply.code(404).send({ error: 'not_found', message: 'No such entry' })
    return { ok: true as const }
  })
}
```

- [ ] **Step 5: Write `routes/me.ts`** and register everything

```ts
import type { FastifyInstance } from 'fastify'
import type { AppDeps } from '../app.js'
import { env } from '../env.js'
import { loadDictionary } from './dictionary.js'
import { getSettings } from './settings.js'

export function registerMe(app: FastifyInstance, deps: AppDeps): void {
  app.get('/v1/me', async (req) => ({
    user: req.user,
    settings: getSettings(deps.db, req.user.id),
    dictionary: loadDictionary(deps.db, req.user.id),
    server: { version: deps.version, model: env.llmModel(), concurrency: env.llmConcurrency() },
  }))
}
```

In `app.ts` register `registerMe`, `registerSettings`, `registerDictionary`, and set `deps.settingsFor ??= (userId) => getSettings(deps.db, userId)` before registering refine so the per-user `llmModel` override applies.

- [ ] **Step 6: Run all tests and typecheck**

Run: `cd server && npm test && npm run typecheck`
Expected: all passing.

- [ ] **Step 7: Write `docs/API.md`** as the route table from `docs/PLAN.md` §4.4 plus one curl example per route.

- [ ] **Step 8: Commit**

```bash
git add server/src docs/API.md server/test/routes-me.test.ts
git commit -m "feat(server): /v1/me, settings and dictionary routes"
```

---

### Task 8: Bench CLI (models × reasoning × concurrency) → `docs/PROMPT.md`

**Files:**
- Create: `server/src/cli/bench.ts`, `server/src/cli/bench-fixtures.ts`
- Modify: `docs/PROMPT.md` (append the table)

**Interfaces:**
- Consumes: `makeOllamaCaller`, `refineText`, `buildSystemPrompt`.

- [ ] **Step 1: Write the fixtures**

`server/src/cli/bench-fixtures.ts` — 20 strings: 6 pt-PT with fillers/repeats, 6 en, 3 mixed, 2 with "nova linha"/"new paragraph", 1 question ("o que achas disto"), 1 one-word ("sim"), 1 numbers ("vinte e cinco euros às três e meia"). Export `BENCH_FIXTURES: string[]`.

- [ ] **Step 2: Write `bench.ts`**

```ts
import { makeOllamaCaller } from '../llm/client.js'
import { refineText } from '../llm/refine.js'
import { BENCH_FIXTURES } from './bench-fixtures.js'

const MATRIX: [string, string][] = [['gemma4', 'none'], ['gpt-oss:120b', 'low'], ['qwen3.5', 'none']]
const llm = makeOllamaCaller()
const p = (xs: number[], q: number) => xs.slice().sort((a, b) => a - b)[Math.min(xs.length - 1, Math.floor(xs.length * q))] ?? 0

console.log('| model | reasoning | p50 | p95 | guard rejections | reasoning chars (avg) |')
console.log('|---|---|---|---|---|---|')
for (const [model, reasoning] of MATRIX) {
  const lat: number[] = []; let rejected = 0; let reasoningTotal = 0
  const rows: string[] = []
  for (const raw of BENCH_FIXTURES) {
    const r = await refineText({ raw, dictionary: [{ term: 'Miraside', replacement: null }, { term: 'Convex', replacement: null }], model, reasoning, timeoutMs: 15000 }, llm)
    lat.push(r.llmMs); reasoningTotal += r.reasoningChars
    if (r.fallbackReason) rejected++
    rows.push(`  ${String(r.llmMs).padStart(5)}ms ${r.fallbackReason ?? 'ok'.padEnd(14)} | ${JSON.stringify(raw.slice(0, 48))} → ${JSON.stringify(r.cleaned.slice(0, 80))}`)
  }
  console.log(`| ${model} | ${reasoning} | ${p(lat, 0.5)} ms | ${p(lat, 0.95)} ms | ${rejected}/${BENCH_FIXTURES.length} | ${Math.round(reasoningTotal / BENCH_FIXTURES.length)} |`)
  console.error(`\n=== ${model} ${reasoning} ===\n${rows.join('\n')}`)
}
for (const n of [3, 5]) {
  const t = performance.now()
  const rs = await Promise.all(BENCH_FIXTURES.slice(0, n).map((raw) => refineText({ raw, dictionary: [], model: MATRIX[0]![0], reasoning: MATRIX[0]![1], timeoutMs: 15000 }, llm)))
  console.log(`\nconcurrency ${n}: wall ${Math.round(performance.now() - t)} ms, failures ${rs.filter((r) => r.fallbackReason).length}`)
}
```

- [ ] **Step 3: Run it against Ollama Cloud**

Run: `cd server && LLM_API_KEY=… npm run bench 2>bench-detail.log`
Expected: a markdown table on stdout; per-fixture detail on stderr. Paste the table into `docs/PROMPT.md` under "Bench", and change `LLM_MODEL` default in `env.ts`/`.env.example` only if the table contradicts the spike.

- [ ] **Step 4: Commit**

```bash
git add server/src/cli/bench.ts server/src/cli/bench-fixtures.ts docs/PROMPT.md
git commit -m "feat(server): model bench and recorded prompt/model decision"
```

---

### Task 9: Dockerfile, compose behind the existing Traefik, backups, VPS runbook

**Files:**
- Create: `server/Dockerfile`, `server/.dockerignore`, `deploy/docker-compose.yml`, `deploy/.env.example`, `deploy/VPS.md`

**VPS facts (docs/SPIKES.md, audited 2026-09-09):** Ubuntu 24.04, 2 vCPU / 7.8 GB, root shell. **Traefik already owns ports 80/443** (container `<proxy-container>`, docker provider, `exposedbydefault=false`, entrypoints `web` → redirects to `websecure`, cert resolver `mytlschallenge` via TLS-ALPN, network `<proxy-network>`). ufw already allows 80/443. The ARwatches ports are loopback-only. Miraside deployments live under `/opt/miraside/`. So: **no Caddy, no new ports; join `<proxy-network>` and route with labels.**

- [ ] **Step 1: Write the Dockerfile**

`server/Dockerfile` (full image builds better-sqlite3 if a prebuild is missing; slim image runs it):
```dockerfile
FROM node:22-bookworm AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY tsconfig.json ./
COPY src ./src
RUN npm run build && npm prune --omit=dev

FROM node:22-bookworm-slim
ENV NODE_ENV=production
WORKDIR /app
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/dist ./dist
COPY package.json ./
RUN mkdir -p /data && chown node:node /data
USER node
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD node -e "fetch('http://127.0.0.1:8080/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"
CMD ["node", "dist/index.js"]
```

`server/.dockerignore`: `node_modules`, `dist`, `data`, `.env*`, `test`.

- [ ] **Step 2: Write the compose file**

`deploy/docker-compose.yml`:
```yaml
name: miraside-voice
services:
  voice-api:
    build: ../server
    restart: unless-stopped
    env_file: .env
    environment:
      DATABASE_PATH: /data/voice.db
      PORT: "8080"
    volumes:
      - voice-data:/data
    deploy:
      resources:
        limits:
          memory: 512M
    networks: [default, edge]
    labels:
      traefik.enable: "true"
      traefik.docker.network: <proxy-network>
      traefik.http.routers.voice.rule: Host(`voice.miraside.co`)
      traefik.http.routers.voice.entrypoints: websecure
      traefik.http.routers.voice.tls: "true"
      traefik.http.routers.voice.tls.certresolver: mytlschallenge
      traefik.http.services.voice.loadbalancer.server.port: "8080"

  backup:
    image: alpine:3.20
    restart: unless-stopped
    volumes:
      - voice-data:/data
      - ./backups:/backups
    command: >
      sh -c "apk add --no-cache sqlite >/dev/null &&
      echo '0 3 * * * sqlite3 /data/voice.db \".backup /backups/voice-$$(date +%F).db\" && find /backups -name \"voice-*.db\" -mtime +14 -delete' > /etc/crontabs/root &&
      crond -f -l 8"
    networks: [default]

volumes:
  voice-data:
networks:
  default:
  edge:
    external: true
    name: <proxy-network>
```

The `web` entrypoint already redirects every HTTP request to HTTPS at the Traefik level, so the router only needs `websecure`. Traefik requests the certificate on first request once DNS resolves to the box.

`deploy/.env.example`: the same keys as `server/.env.example` minus `DATABASE_PATH`/`PORT` (set by compose).

- [ ] **Step 3: Local smoke test of the image**

Run:
```bash
cd server && docker build -t miraside-voice-api:dev . && docker run --rm -d --name voice-smoke -e LLM_API_KEY=x -e DATABASE_PATH=/data/voice.db -p 18080:8080 miraside-voice-api:dev && sleep 3 && curl -s localhost:18080/health && docker exec voice-smoke node dist/cli/users.js add Smoke && docker rm -f voice-smoke
```
Expected: `{"ok":true,...,"db":"ok","llm":"degraded"}`, a token line from the CLI inside the container. Also `docker compose -f deploy/docker-compose.yml config` must render (it needs `<proxy-network>` to exist only at `up` time, not at `config` time).

- [ ] **Step 4: Write `deploy/VPS.md`**

Sections, each a copy-pasteable block:
1. **Access.** `ssh -i ~/.ssh/<SSH_KEY> -o IdentitiesOnly=yes vps` (root shell). Nothing to open: Traefik already listens on 80/443 and ufw allows them.
2. **Port audit (sanity, every deploy).** `docker ps --format '{{.Names}}\t{{.Ports}}'` — only `<proxy-container>` may show `0.0.0.0`. Docker bypasses ufw, so anything else on `0.0.0.0` is already public.
3. **DNS.** Namecheap → `voice` A record → `<VPS_IP>` (no wildcard exists). Wait for `dig +short voice.miraside.co`.
4. **Deploy.** `mkdir -p /opt/miraside && cd /opt/miraside && git clone git@github.com:<org>/miraside-voice.git voice && cd voice/deploy && cp .env.example .env && $EDITOR .env && mkdir -p backups && docker compose up -d --build && docker compose ps`.
5. **Verify.** `curl -s https://voice.miraside.co/health`; `docker compose logs -f voice-api`; certificate issuance in `docker logs <proxy-container> --since 5m | grep -i voice`.
6. **Users.** `docker compose exec voice-api node dist/cli/users.js add "Miguel" --label MacBook` (token shown once), `… list`, `… revoke <tokenId>`.
7. **Update.** `git pull && docker compose up -d --build`. **Rollback.** `git checkout <sha> && docker compose up -d --build`.
8. **Backups.** nightly in `deploy/backups/`, 14 kept. **Restore.** `docker compose stop voice-api && docker run --rm -v miraside-voice_voice-data:/data -v $PWD/backups:/b alpine cp /b/voice-YYYY-MM-DD.db /data/voice.db && docker compose start voice-api`.
9. **Do not touch** `/docker/n8n` (Traefik + n8n), `/root/ARwatches` or `/opt/miraside/demos` — other projects share the box.

- [ ] **Step 5: Commit**

```bash
git add server/Dockerfile server/.dockerignore deploy
git commit -m "feat(deploy): Dockerfile, compose behind the shared Traefik, nightly backups, VPS runbook"
```

---

### Task 10: First deploy to the VPS (M0 acceptance)

**Blocked on Miguel:** the Namecheap A record `voice.miraside.co → <VPS_IP>`. SSH works with `~/.ssh/<SSH_KEY>`.

- [ ] **Step 1:** Follow `deploy/VPS.md` §1–§5.
- [ ] **Step 2:** `curl -s https://voice.miraside.co/health` → `{"ok":true,"db":"ok","llm":"ok"}`.
- [ ] **Step 3:** Issue Miguel's token (§6) and store it in the password manager for the Mac Settings › Server tab.
- [ ] **Step 4:** `docker ps` shows the arwatches, n8n, deal-pipeline and miraside-voice projects; nothing else changed.
- [ ] **Step 5:** Update `docs/SPIKES.md` VPS section with the post-deploy port audit output.

---

## Self-review

- **Spec coverage:** §4.1 schema (Task 2), §4.2 auth + rate limit (Task 3), §4.3 refine incl. token cap, semaphore, think strip, guards, noise, bench (Tasks 4, 5, 8), §4.4 all routes (Tasks 6, 7), §4.5 logging (Task 6: metadata only, transcripts only under the dev flag), §4.6 env (Task 1), §5 deployment as revised for the existing Traefik (Tasks 9, 10), §6 fallback enum (Task 2 schema + Task 6), §7 server-side security (Tasks 3, 9).
- **Placeholders:** none; the one deliberately flagged assert in Task 2 Step 1 is spelled out.
- **Type consistency:** `LlmCaller`/`LlmRequest`/`LlmResponse` (Task 4) are what Tasks 5–8 consume; `FallbackReason`/`Injected` come from `db/schema.ts`; `upsertDictation` is used identically in Tasks 6 and 7; `AppDeps` grows in Task 6 and is final there.

import Fastify, { type FastifyInstance, type FastifyError } from 'fastify'
import helmet from '@fastify/helmet'
import rateLimit from '@fastify/rate-limit'
import { serializerCompiler, validatorCompiler, type ZodTypeProvider } from 'fastify-type-provider-zod'
import { env } from './env.js'
import type { Db } from './db/client.js'
import { authenticate, parseBearer, hashToken, FailedAuthLimiter, type AuthUser } from './auth.js'
import type { LlmCaller } from './llm/client.js'
import { Semaphore } from './llm/semaphore.js'
import { backfillWordCounts } from './dictations-repo.js'
import { registerRefine } from './routes/refine.js'
import { registerDictations } from './routes/dictations.js'
import { registerDictionary } from './routes/dictionary.js'
import { registerMe } from './routes/me.js'
import { registerSettings, getSettings } from './routes/settings.js'

declare module 'fastify' {
  interface FastifyRequest {
    user: AuthUser
    tokenId: string
  }
}

export interface AppDeps {
  db: Db
  llm: LlmCaller
  version: string
  llmSemaphore?: Semaphore
  probeLlm?: () => Promise<string[]>
  settingsFor?: (userId: string) => { llmModel?: string } | null
}

const LLM_PROBE_CACHE_MS = 60_000

export function buildApp(deps: AppDeps): FastifyInstance {
  const app = Fastify({
    logger: {
      level: process.env.LOG_LEVEL ?? 'info',
      // Never let a transcript into the log through a serialized request body.
      serializers: { req: (r) => ({ method: r.method, url: r.url, id: r.id }) },
    },
    bodyLimit: 64 * 1024,
    requestIdHeader: 'x-request-id',
    // This box sits behind Traefik on the shared `<proxy-network>` docker network — no
    // port is published to the host, so only other containers on that network (or
    // this stack's own) can reach voice-api at all. `trustProxy: 1` (Express-style
    // hop counting) is what a naive fix reaches for here, but Fastify 5.12+ makes it
    // a permanent no-op ("Hop-count-only trust... lets direct clients spoof
    // X-Forwarded-* values", docs/Reference/Server.md) — verified against this
    // exact dependency version, see docs/API.md / final-fix-report for the deviation
    // note. Trusting the private-network presets instead (not `true`) means
    // X-Forwarded-For is honoured from anything on the docker network, same as
    // `true` would allow in THIS topology (nothing public can reach this port
    // directly either way) — it is not a defense against another container on
    // `<proxy-network>` (e.g. n8n itself) spoofing the header, which no `trustProxy`
    // value can fix from inside this process; that gap is pre-existing infra risk,
    // not something this wave closes.
    trustProxy: 'loopback,uniquelocal',
  })
  app.setValidatorCompiler(validatorCompiler)
  app.setSerializerCompiler(serializerCompiler)
  app.register(helmet, { global: true })
  // Placeholder until the auth hook below sets the real value for every non-health route.
  app.decorateRequest('user', null as unknown as AuthUser)
  app.decorateRequest('tokenId', '')
  app.register(rateLimit, {
    max: 120,
    timeWindow: '1 minute',
    // Key by the token hash, never the raw bearer; unauthenticated callers share a per-IP bucket.
    keyGenerator: (req) => {
      const token = parseBearer(req.headers.authorization)
      return token ? hashToken(token) : `ip:${req.ip}`
    },
  })
  // Per-IP failed-authentication counter (spec §4.2): stops a scanner from filling
  // the logs with 401s. @fastify/rate-limit's keyGenerator alone can't do this
  // because every random well-formed token gets its own bucket.
  const failedAuthLimiter = new FailedAuthLimiter()
  app.addHook('onRequest', async (req, reply) => {
    // routeOptions.url is the registered route pattern (e.g. '/health'), populated by
    // the time onRequest runs (routing happens before onRequest in the Fastify
    // lifecycle) — unlike req.url, it is never polluted by a query string.
    if (req.routeOptions.url === '/health') return
    // Authenticate before ever consulting the limiter: a scanner sending junk tokens
    // must never be able to lock out a real device sharing its IP (NAT, VPN, the
    // office wifi) by burning through the failed-auth ceiling first.
    const result = authenticate(deps.db, req.headers.authorization)
    if (result.ok) {
      req.user = result.user
      req.tokenId = result.tokenId
      return
    }
    if (failedAuthLimiter.shouldBlock(req.ip, Date.now())) {
      return reply.code(429).send({ error: 'rate_limited', message: 'Too many failed authentications' })
    }
    failedAuthLimiter.record(req.ip, Date.now())
    return reply.code(401).send({ error: result.error, message: 'Authentication failed' })
  })
  app.addHook('onSend', async (req, reply) => {
    reply.header('x-request-id', req.id)
  })

  // Never leak internals (stack traces, driver error text) to a client. 4xx errors
  // (zod validation, body-too-large, …) fall through unchanged by rethrowing —
  // Fastify's own default handler formats those exactly as before this hook existed.
  app.setErrorHandler((err: FastifyError, req, reply) => {
    const statusCode = err.statusCode ?? 500
    if (statusCode >= 500) {
      req.log.error({ err }) // never the request body — it may hold a transcript
      return reply.code(500).send({ error: 'server', message: 'Internal error' })
    }
    throw err
  })

  // A closed db after app.close() (SIGTERM/SIGINT) checkpoints the WAL back into the
  // main file, which is what makes the backup+restore runbook (deploy/VPS.md §8)
  // trustworthy — a kill -9 or crash still leaves an un-checkpointed WAL behind.
  app.addHook('onClose', () => {
    deps.db.raw.close()
  })

  // Rows written before `word_count` existed have NULL there and are excluded from every word
  // total until they are counted (rule 7). Non-fatal on purpose: a table that is only partly
  // backfilled understates the headline number, which is visibly wrong and fixes itself on the
  // next boot — refusing to start would mean not accepting dictations at all, which is worse.
  try {
    const filled = backfillWordCounts(deps.db)
    if (filled > 0) app.log.info({ filled }, 'backfilled word_count')
  } catch (err) {
    app.log.error({ err }, 'word_count backfill failed; totals will understate until the next boot')
  }

  // Per-user llmModel override (settings) must be wired before refine reads it.
  deps.settingsFor ??= (userId) => getSettings(deps.db, userId)
  deps.llmSemaphore ??= new Semaphore(env.llmConcurrency())

  // Routes are declared inside `app.after` so @fastify/rate-limit's `onRoute` hook
  // (registered by the deferred `app.register` above) exists before any route is
  // added — otherwise the 120 req/min limiter never attaches to a single one of them.
  app.after(() => {
    registerRefine(app, deps)
    registerDictations(app, deps)
    registerDictionary(app, deps)
    registerMe(app, deps)
    registerSettings(app, deps)
  })

  // Cached per app instance: a probe never fires more than once per 60s and never
  // throws past this function, so /health always answers 200.
  let llmProbeCache: { status: 'ok' | 'degraded'; expiresAt: number } | null = null
  async function probeLlmStatus(): Promise<'ok' | 'degraded' | 'unknown'> {
    if (!deps.probeLlm) return 'unknown'
    const now = Date.now()
    if (llmProbeCache && llmProbeCache.expiresAt > now) return llmProbeCache.status
    let status: 'ok' | 'degraded' = 'ok'
    try {
      await deps.probeLlm()
    } catch {
      status = 'degraded'
    }
    llmProbeCache = { status, expiresAt: now + LLM_PROBE_CACHE_MS }
    return status
  }

  app.withTypeProvider<ZodTypeProvider>().get('/health', async () => {
    let db: 'ok' | 'error' = 'ok'
    try {
      deps.db.raw.prepare('select 1').get()
    } catch {
      db = 'error'
    }
    const llm = await probeLlmStatus()
    return {
      ok: true,
      version: deps.version,
      db,
      llm,
    }
  })

  return app
}

export type App = ReturnType<typeof buildApp>
export { env }

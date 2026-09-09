import Fastify, { type FastifyInstance } from 'fastify'
import helmet from '@fastify/helmet'
import rateLimit from '@fastify/rate-limit'
import { serializerCompiler, validatorCompiler, type ZodTypeProvider } from 'fastify-type-provider-zod'
import { env } from './env.js'
import type { Db } from './db/client.js'
import { authenticate, hashToken, FailedAuthLimiter, type AuthUser } from './auth.js'
import type { LlmCaller } from './llm/client.js'
import type { Semaphore } from './llm/semaphore.js'
import { registerRefine } from './routes/refine.js'
import { registerDictations } from './routes/dictations.js'

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
      const m = req.headers.authorization?.match(/^Bearer\s+(mv_[A-Za-z0-9_-]+)$/)
      return m ? hashToken(m[1]!) : `ip:${req.ip}`
    },
  })
  // Per-IP failed-authentication counter (spec §4.2): stops a scanner from filling
  // the logs with 401s. @fastify/rate-limit's keyGenerator alone can't do this
  // because every random well-formed token gets its own bucket.
  const failedAuthLimiter = new FailedAuthLimiter()
  app.addHook('onRequest', async (req, reply) => {
    if (req.url === '/health') return
    if (failedAuthLimiter.shouldBlock(req.ip, Date.now())) {
      return reply.code(429).send({ error: 'rate_limited', message: 'Too many failed authentications' })
    }
    const result = authenticate(deps.db, req.headers.authorization)
    if (!result.ok) {
      failedAuthLimiter.record(req.ip, Date.now())
      return reply.code(401).send({ error: result.error, message: 'Authentication failed' })
    }
    req.user = result.user
    req.tokenId = result.tokenId
  })
  app.addHook('onSend', async (req, reply) => {
    reply.header('x-request-id', req.id)
  })

  registerRefine(app, deps)
  registerDictations(app, deps)

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

import Fastify, { type FastifyInstance } from 'fastify'
import helmet from '@fastify/helmet'
import rateLimit from '@fastify/rate-limit'
import { serializerCompiler, validatorCompiler, type ZodTypeProvider } from 'fastify-type-provider-zod'
import { env } from './env.js'
import type { Db } from './db/client.js'
import { authenticate, hashToken, FailedAuthLimiter, type AuthUser } from './auth.js'

declare module 'fastify' {
  interface FastifyRequest {
    user: AuthUser
    tokenId: string
  }
}

export interface AppDeps {
  db: Db
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

  app.withTypeProvider<ZodTypeProvider>().get('/health', async () => {
    let db: 'ok' | 'error' = 'ok'
    try {
      deps.db.raw.prepare('select 1').get()
    } catch {
      db = 'error'
    }
    return {
      ok: true,
      version: deps.version,
      db,
      llm: 'unknown',
    }
  })

  return app
}

export type App = ReturnType<typeof buildApp>
export { env }

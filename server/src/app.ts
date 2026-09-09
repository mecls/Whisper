import Fastify, { type FastifyInstance } from 'fastify'
import helmet from '@fastify/helmet'
import { serializerCompiler, validatorCompiler, type ZodTypeProvider } from 'fastify-type-provider-zod'
import { env } from './env.js'
import type { Db } from './db/client.js'

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

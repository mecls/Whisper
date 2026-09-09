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

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

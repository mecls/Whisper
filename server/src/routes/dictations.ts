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

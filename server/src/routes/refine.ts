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

    let cleaned: string
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
      const release = deps.llmSemaphore ? deps.llmSemaphore.tryAcquire() : () => {}
      if (release === null) {
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
      // totalMs is unknowable here: the paste happens after this call returns. The client
      // supplies it on the follow-up PATCH, which coalesces it into this row.
      totalMs: null,
      llmModel: usedModel, clientVersion: b.clientVersion, fallbackReason,
    })
    req.log.info({ userId: user.id, rawChars: b.raw.length, cleanedChars: cleaned.length, llmMs, model: usedModel, fallbackReason, mode: b.mode, ...logExtra }, 'refine')
    if (env.logTranscripts()) req.log.debug({ raw: b.raw, cleaned }, 'refine transcript (dev only)')
    return { clientId: b.clientId, cleaned, raw: b.raw, model: usedModel, llmMs, fallbackReason }
  })
}

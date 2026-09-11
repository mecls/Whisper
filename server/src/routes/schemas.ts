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

/**
 * The outbox/replay body. It now also carries cleanup that happened somewhere other than
 * `/v1/refine` — on the client, or not at all when the skip gate decided the transcript did not
 * need it. All four are nullable with a null default so an older client, which sends none of
 * them, still validates instead of 400-ing its dictations into a permanent outbox loop.
 */
export const DictationBody = RefineBody.omit({ budgetMs: true }).extend({
  injected: z.enum(INJECTED),
  fallbackReason: z.enum(FALLBACK_REASONS).nullable().default(null),
  cleaned: z.string().max(60_000).nullable().default(null),
  llmMs: z.number().int().nonnegative().max(600_000).nullable().default(null),
  llmModel: z.string().max(80).nullable().default(null),
  totalMs: z.number().int().nonnegative().max(600_000).nullable().default(null),
})
export type DictationBody = z.infer<typeof DictationBody>

// `totalMs` rides the PATCH because release→paste is only known after the paste, which is after
// /v1/refine has already written the row. `nullish`, not `optional`: Swift's synthesized Encodable
// writes a nil Optional as an explicit `null` rather than omitting the key (see the C3 note on
// ServerSettings in VoiceAPI.swift), so an unmeasured dictation arrives as `totalMs: null` and a
// plain `.optional()` would 400 it. Null and absent both mean "not measured" and both must leave
// any stored measurement untouched — see setInjected.
export const InjectedBody = z.object({
  injected: z.enum(INJECTED),
  totalMs: z.number().int().nonnegative().max(600_000).nullish(),
})

export const ErrorReply = z.object({ error: z.string(), message: z.string() })

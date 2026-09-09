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

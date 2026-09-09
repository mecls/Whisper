import type { LlmCaller } from './client.js'
import { classifyLlmError } from './errors.js'
import { checkGuards, estimateTokens, isNoise, normalizeOutput } from './guards.js'
import { buildSystemPrompt, type DictionaryTerm } from './prompt.js'
import type { FallbackReason } from '../db/schema.js'

export interface RefineInput {
  raw: string
  dictionary: DictionaryTerm[]
  model: string
  reasoning: string
  timeoutMs: number
}
export interface RefineOutcome {
  cleaned: string
  fallbackReason: FallbackReason | null
  llmMs: number
  model: string
  reasoningChars: number
  completionTokens: number | null
  errorKind?: string
  guardReason?: string
}

/** Token cap counts the reasoning channel too (Ollama maps max_tokens to num_predict). Floor 512. */
export function maxTokensFor(raw: string): number {
  return Math.max(512, estimateTokens(raw) * 3 + 192)
}

export async function refineText(input: RefineInput, llm: LlmCaller): Promise<RefineOutcome> {
  const t0 = performance.now()
  const base = { model: input.model, reasoningChars: 0, completionTokens: null as number | null }
  try {
    const res = await llm({
      system: buildSystemPrompt(input.dictionary),
      user: input.raw,
      model: input.model,
      reasoning: input.reasoning,
      maxTokens: maxTokensFor(input.raw),
      timeoutMs: input.timeoutMs,
    })
    const llmMs = Math.round(performance.now() - t0)
    const meta = { ...base, llmMs, reasoningChars: res.reasoningChars, completionTokens: res.completionTokens }
    if (res.finishReason === 'length') return { ...meta, cleaned: input.raw, fallbackReason: 'llm-truncated' }
    const cleaned = normalizeOutput(res.content)
    if (cleaned.length === 0 && isNoise(input.raw)) return { ...meta, cleaned: '', fallbackReason: null }
    const guard = checkGuards(input.raw, cleaned)
    if (!guard.ok) return { ...meta, cleaned: input.raw, fallbackReason: 'guard-rejected', guardReason: guard.reason }
    return { ...meta, cleaned, fallbackReason: null }
  } catch (err) {
    const llmMs = Math.round(performance.now() - t0)
    const info = classifyLlmError(err)
    const fallbackReason: FallbackReason = info.kind === 'timeout' ? 'llm-timeout' : 'llm-error'
    return { ...base, llmMs, cleaned: input.raw, fallbackReason, errorKind: info.kind }
  }
}

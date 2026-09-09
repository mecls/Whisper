import OpenAI from 'openai'
import { env } from '../env.js'

export interface LlmRequest {
  system: string
  user: string
  model: string
  reasoning: string // none | low | medium | high
  maxTokens: number
  timeoutMs: number
}
export interface LlmResponse {
  content: string
  finishReason: string | null
  completionTokens: number | null
  /** Ollama returns thinking in `message.reasoning`; we only measure it. */
  reasoningChars: number
}
export type LlmCaller = (req: LlmRequest) => Promise<LlmResponse>

let cached: OpenAI | null = null
/** Cached singleton (pattern from hub/tools/ContentAgent/lib/agent/llm.ts). No SDK retries: the Mac owns the budget. */
export function ollamaClient(): OpenAI {
  cached ??= new OpenAI({ baseURL: env.llmBaseUrl(), apiKey: env.llmApiKey(), maxRetries: 0 })
  return cached
}

export function makeOllamaCaller(): LlmCaller {
  return async (req) => {
    const body: Record<string, unknown> = {
      model: req.model,
      messages: [{ role: 'system', content: req.system }, { role: 'user', content: req.user }],
      temperature: 0.1,
      max_tokens: req.maxTokens,
      stream: false,
    }
    if (req.reasoning && req.reasoning !== 'default') body.reasoning_effort = req.reasoning
    const res = await ollamaClient().chat.completions.create(body as never, { timeout: req.timeoutMs })
    const choice = res.choices?.[0]
    const msg = (choice?.message ?? {}) as { content?: string | null; reasoning?: string | null }
    return {
      content: msg.content ?? '',
      finishReason: choice?.finish_reason ?? null,
      completionTokens: res.usage?.completion_tokens ?? null,
      reasoningChars: (msg.reasoning ?? '').length,
    }
  }
}

/** Health probe: a models list, never a completion (a completion would occupy a concurrency slot). */
export async function probeModels(): Promise<string[]> {
  const list = await ollamaClient().models.list({ timeout: 5000 } as never)
  return list.data.map((m) => m.id)
}

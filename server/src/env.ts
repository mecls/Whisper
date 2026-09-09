/**
 * Typed environment access (pattern from hub/LoanAgent/lib/env.ts).
 *
 * Values are read lazily through getters that throw a clear error when
 * missing, so a missing key surfaces at the call site, not as `undefined`
 * deep inside the SDK.
 */
function required(name: string): string {
  const v = process.env[name]
  if (!v) throw new Error(`Missing required environment variable: ${name}`)
  return v
}

function intOr(name: string, fallback: number): number {
  const v = process.env[name]
  if (!v) return fallback
  const n = Number.parseInt(v, 10)
  if (!Number.isFinite(n)) throw new Error(`Environment variable ${name} must be an integer, got ${JSON.stringify(v)}`)
  return n
}

export const env = {
  llmBaseUrl: () => process.env.LLM_BASE_URL ?? 'https://ollama.com/v1',
  llmApiKey: () => required('LLM_API_KEY'),
  // Spike (docs/SPIKES.md): gemma4 at `none` was the fastest clean model.
  llmModel: () => process.env.LLM_MODEL ?? 'gemma4',
  llmReasoning: () => process.env.LLM_REASONING ?? 'none',
  // Ollama Cloud caps in-flight requests per plan; the account handled 5.
  llmConcurrency: () => intOr('LLM_CONCURRENCY', 5),
  // Ceiling for the per-request budget the Mac sends.
  llmMaxTimeoutMs: () => intOr('LLM_MAX_TIMEOUT_MS', 14000),
  databasePath: () => process.env.DATABASE_PATH ?? './data/voice.db',
  port: () => intOr('PORT', 8080),
  /** Transcripts in logs are a dev-only aid; production refuses the flag outright. */
  logTranscripts: () => {
    const on = process.env.LOG_TRANSCRIPTS === '1'
    if (on && process.env.NODE_ENV === 'production') {
      throw new Error('LOG_TRANSCRIPTS is not allowed in production')
    }
    return on
  },
} as const

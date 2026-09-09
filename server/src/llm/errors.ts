// Turning a provider failure into something a caller can act on.
//
// ── The trap ────────────────────────────────────────────────────────────────
// The OpenAI SDK does `const error = errorResponse?.['error']` when building an
// APIError. Ollama Cloud's body is `{"error": "<string>"}`, so `err.error` is a
// plain STRING, not the `{ message }` object the OpenAI API returns. A
// classifier that reads `err.error.message` finds `undefined` and silently
// falls through to 'unknown' for every Ollama failure.

export type LlmErrorKind =
  | 'auth' | 'billing' | 'entitlement' | 'model-not-found' | 'rate-limit'
  | 'timeout' | 'network' | 'server' | 'config' | 'unknown'

export interface LlmErrorInfo {
  kind: LlmErrorKind
  status?: number
  /** Short, safe to log. Never contains prompt or transcript content. */
  detail: string
}

function bodyText(err: unknown): string {
  const e = err as { error?: unknown; message?: unknown } | null
  const body = e?.error
  if (typeof body === 'string') return body
  if (body && typeof body === 'object') {
    const m = (body as { message?: unknown }).message
    if (typeof m === 'string') return m
  }
  return typeof e?.message === 'string' ? e.message : String(err)
}

export function classifyLlmError(err: unknown): LlmErrorInfo {
  const status = (err as { status?: number } | null)?.status
  const name = (err as { name?: string } | null)?.name
  const body = bodyText(err)
  const detail = body.slice(0, 240)

  if (name === 'APIConnectionTimeoutError') return { kind: 'timeout', detail }
  if (name === 'APIConnectionError') return { kind: 'network', detail }
  if (status === 401) return { kind: 'auth', status, detail }
  // Ollama returns 402 for a model billed against a separate "extra usage" balance when it is empty.
  if (status === 402) return { kind: 'billing', status, detail }
  if (status === 403) {
    return { kind: /past due|payment|billing/i.test(body) ? 'billing' : 'entitlement', status, detail }
  }
  if (status === 404) return { kind: 'model-not-found', status, detail }
  if (status === 429) return { kind: 'rate-limit', status, detail }
  if (typeof status === 'number' && status >= 500) return { kind: 'server', status, detail }
  if (/Missing required environment variable/i.test(body)) return { kind: 'config', detail }
  return { kind: 'unknown', status, detail }
}

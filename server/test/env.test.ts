import { test } from 'node:test'
import assert from 'node:assert/strict'
import { env } from '../src/env.js'

test('required vars throw a named error when missing', () => {
  delete process.env.LLM_API_KEY
  assert.throws(() => env.llmApiKey(), /Missing required environment variable: LLM_API_KEY/)
})

test('defaults come from the spike', () => {
  delete process.env.LLM_MODEL
  delete process.env.LLM_REASONING
  delete process.env.LLM_CONCURRENCY
  assert.equal(env.llmModel(), 'gemma4')
  assert.equal(env.llmReasoning(), 'none')
  assert.equal(env.llmConcurrency(), 5)
  assert.equal(env.llmMaxTimeoutMs(), 14000)
  assert.equal(env.port(), 8080)
})

test('LOG_TRANSCRIPTS is refused in production', () => {
  process.env.NODE_ENV = 'production'
  process.env.LOG_TRANSCRIPTS = '1'
  assert.throws(() => env.logTranscripts(), /LOG_TRANSCRIPTS is not allowed in production/)
  process.env.NODE_ENV = 'test'
  assert.equal(env.logTranscripts(), true)
  process.env.LOG_TRANSCRIPTS = '0'
  assert.equal(env.logTranscripts(), false)
})

// Promoted minor: PORT, LLM_CONCURRENCY and LLM_MAX_TIMEOUT_MS must reject <1 so a
// misconfigured prod .env (e.g. PORT=0) fails loudly at boot instead of producing a
// nonsensical listener or a concurrency limit that admits no requests at all.
test('intOr rejects values below 1 for PORT, LLM_CONCURRENCY and LLM_MAX_TIMEOUT_MS', () => {
  try {
    process.env.PORT = '0'
    assert.throws(() => env.port(), /Environment variable PORT must be >= 1/)
    process.env.PORT = '-3'
    assert.throws(() => env.port(), /Environment variable PORT must be >= 1/)

    process.env.LLM_CONCURRENCY = '0'
    assert.throws(() => env.llmConcurrency(), /Environment variable LLM_CONCURRENCY must be >= 1/)

    process.env.LLM_MAX_TIMEOUT_MS = '0'
    assert.throws(() => env.llmMaxTimeoutMs(), /Environment variable LLM_MAX_TIMEOUT_MS must be >= 1/)
  } finally {
    delete process.env.PORT
    delete process.env.LLM_CONCURRENCY
    delete process.env.LLM_MAX_TIMEOUT_MS
  }
})

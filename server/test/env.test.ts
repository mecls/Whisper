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

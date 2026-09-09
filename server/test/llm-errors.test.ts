import { test } from 'node:test'
import assert from 'node:assert/strict'
import { classifyLlmError } from '../src/llm/errors.js'

test('reads Ollama string bodies and OpenAI object bodies', () => {
  assert.equal(classifyLlmError({ status: 401, error: 'invalid api key' }).kind, 'auth')
  assert.equal(classifyLlmError({ status: 402, error: 'extra usage balance empty' }).kind, 'billing')
  assert.equal(classifyLlmError({ status: 403, error: 'payment past due' }).kind, 'billing')
  assert.equal(classifyLlmError({ status: 403, error: 'model not in plan' }).kind, 'entitlement')
  assert.equal(classifyLlmError({ status: 404, error: { message: 'model x not found' } }).kind, 'model-not-found')
  assert.equal(classifyLlmError({ status: 429, error: 'slow down' }).kind, 'rate-limit')
  assert.equal(classifyLlmError({ status: 503, error: 'busy' }).kind, 'server')
  assert.equal(classifyLlmError({ name: 'APIConnectionTimeoutError', message: 'timed out' }).kind, 'timeout')
  assert.equal(classifyLlmError({ name: 'APIConnectionError', message: 'ECONNRESET' }).kind, 'network')
  assert.equal(classifyLlmError(new Error('Missing required environment variable: LLM_API_KEY')).kind, 'config')
  const d = classifyLlmError({ status: 500, error: 'x'.repeat(500) })
  assert.equal(d.detail.length, 240)
})

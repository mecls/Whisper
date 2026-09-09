import { test } from 'node:test'
import assert from 'node:assert/strict'
import { refineText } from '../src/llm/refine.js'
import { buildSystemPrompt } from '../src/llm/prompt.js'
import type { LlmCaller } from '../src/llm/client.js'

const raw = 'hã então eu acho que que amanhã vamos vamos fechar o contrato tipo às três da tarde'
const base = { raw, dictionary: [], model: 'gemma4', reasoning: 'none', timeoutMs: 3000 }
const ok = (content: string, finish = 'stop'): LlmCaller => async () => ({ content, finishReason: finish, completionTokens: 20, reasoningChars: 0 })

test('happy path returns the normalized cleaned text', async () => {
  const r = await refineText(base, ok('"Eu acho que amanhã vamos fechar o contrato às três da tarde."'))
  assert.equal(r.cleaned, 'Eu acho que amanhã vamos fechar o contrato às três da tarde.')
  assert.equal(r.fallbackReason, null)
  assert.equal(r.model, 'gemma4')
})

test('guard rejection falls back to raw', async () => {
  const r = await refineText(base, ok('Ok.'))
  assert.equal(r.cleaned, raw)
  assert.equal(r.fallbackReason, 'guard-rejected')
})

test('truncation, timeout and errors map to their reasons', async () => {
  assert.equal((await refineText(base, ok('Eu acho que amanhã vamos fechar', 'length'))).fallbackReason, 'llm-truncated')
  const timeout: LlmCaller = async () => { throw Object.assign(new Error('t'), { name: 'APIConnectionTimeoutError' }) }
  assert.equal((await refineText(base, timeout)).fallbackReason, 'llm-timeout')
  const boom: LlmCaller = async () => { throw Object.assign(new Error('x'), { status: 503, error: 'busy' }) }
  const r = await refineText(base, boom)
  assert.equal(r.fallbackReason, 'llm-error')
  assert.equal(r.errorKind, 'server')
  assert.equal(r.cleaned, raw)
})

test('max_tokens floors at 512 and scales with input', async () => {
  let seen = 0
  const spy: LlmCaller = async (req) => { seen = req.maxTokens; return { content: 'x'.repeat(30), finishReason: 'stop', completionTokens: 1, reasoningChars: 0 } }
  await refineText({ ...base, raw: 'sim' }, spy)
  assert.equal(seen, 512)
  await refineText({ ...base, raw: 'a'.repeat(3500) }, spy)
  assert.equal(seen, 1000 * 3 + 192)
})

test('system prompt embeds the dictionary', () => {
  const p = buildSystemPrompt([{ term: 'Miraside', replacement: null }, { term: 'convex', replacement: 'Convex' }])
  assert.match(p, /Miraside/)
  assert.match(p, /convex.*→.*Convex|"convex" as "Convex"/)
  assert.match(p, /Output ONLY the cleaned text/)
})

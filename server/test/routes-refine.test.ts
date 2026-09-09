import { test } from 'node:test'
import assert from 'node:assert/strict'
import { buildApp } from '../src/app.js'
import { openDb } from '../src/db/client.js'
import { createUser, issueToken } from '../src/auth.js'
import { dictations } from '../src/db/schema.js'
import type { LlmCaller } from '../src/llm/client.js'
import { Semaphore } from '../src/llm/semaphore.js'

const raw = 'hã então eu acho que que amanhã vamos vamos fechar o contrato tipo às três da tarde'
const body = (over: Record<string, unknown> = {}) => ({
  clientId: '11111111-1111-4111-8111-111111111111', raw, mode: 'clean', languageSetting: 'auto', languageDetected: 'pt',
  budgetMs: 4000, context: { appBundleId: 'com.apple.mail', appName: 'Mail' }, timing: { audioMs: 6000, asrMs: 1400 },
  asrModel: 'large-v3-turbo-632MB', clientVersion: '0.1.0', createdAt: 1757400000000, ...over,
})

function setup(llm: LlmCaller, semaphore = new Semaphore(5)) {
  const db = openDb(':memory:')
  const u = createUser(db, 'Miguel')
  const token = issueToken(db, u.id, 'test')
  const app = buildApp({ db, llm, version: 'test', llmSemaphore: semaphore })
  const post = (url: string, payload: unknown, method: 'POST' | 'PATCH' = 'POST') =>
    app.inject({ method, url, payload, headers: { authorization: `Bearer ${token}` } })
  return { db, app, post, token, userId: u.id }
}
const good: LlmCaller = async () => ({ content: 'Eu acho que amanhã vamos fechar o contrato às três da tarde.', finishReason: 'stop', completionTokens: 20, reasoningChars: 0 })

test('refine returns cleaned text and stores one row; a replay upserts, not duplicates', async () => {
  const { db, app, post } = setup(good)
  const r1 = await post('/v1/refine', body())
  assert.equal(r1.statusCode, 200)
  assert.equal(r1.json().cleaned, 'Eu acho que amanhã vamos fechar o contrato às três da tarde.')
  assert.equal(r1.json().fallbackReason, null)
  const r2 = await post('/v1/dictations', { ...body(), injected: 'raw', fallbackReason: 'client-timeout' })
  assert.equal(r2.statusCode, 200)
  const rows = db.select().from(dictations).all()
  assert.equal(rows.length, 1)
  assert.equal(rows[0]!.cleaned, 'Eu acho que amanhã vamos fechar o contrato às três da tarde.') // replay never erases the cleanup
  assert.equal(rows[0]!.injected, 'raw')
  assert.equal(rows[0]!.fallbackReason, 'client-timeout')
  await app.close()
})

test('literal mode skips the LLM; noise returns empty and injected none', async () => {
  let calls = 0
  const { app, post, db } = setup(async () => { calls++; return { content: 'x', finishReason: 'stop', completionTokens: 1, reasoningChars: 0 } })
  const lit = await post('/v1/refine', body({ mode: 'literal', clientId: 'a' }))
  assert.equal(lit.json().cleaned, raw)
  const noise = await post('/v1/refine', body({ raw: 'hã hã', clientId: 'b' }))
  assert.equal(noise.json().cleaned, '')
  assert.equal(calls, 0)
  const rows = db.select().from(dictations).all()
  assert.equal(rows.find((r) => r.clientId === 'b')?.injected, 'none')
  await app.close()
})

test('busy semaphore fails fast with llm-busy and raw text', async () => {
  const sem = new Semaphore(1)
  const release = sem.tryAcquire()!
  const { app, post } = setup(good, sem)
  const r = await post('/v1/refine', body())
  assert.equal(r.json().cleaned, raw)
  assert.equal(r.json().fallbackReason, 'llm-busy')
  release()
  await app.close()
})

test('server timeout is budgetMs - 700 capped by LLM_MAX_TIMEOUT_MS', async () => {
  let seen = 0
  const { app, post } = setup(async (req) => { seen = req.timeoutMs; return { content: 'Eu acho que amanhã vamos fechar o contrato às três da tarde.', finishReason: 'stop', completionTokens: 1, reasoningChars: 0 } })
  await post('/v1/refine', body({ budgetMs: 4000 }))
  assert.equal(seen, 3300)
  await post('/v1/refine', body({ budgetMs: 60000, clientId: 'c' }))
  assert.equal(seen, 14000)
  await app.close()
})

test('PATCH by-client sets injected; history lists newest first', async () => {
  const { app, post, token } = setup(good)
  await post('/v1/refine', body())
  const p = await post('/v1/dictations/by-client/11111111-1111-4111-8111-111111111111', { injected: 'cleaned' }, 'PATCH')
  assert.equal(p.statusCode, 200)
  const missing = await post('/v1/dictations/by-client/nope', { injected: 'cleaned' }, 'PATCH')
  assert.equal(missing.statusCode, 404)
  const list = await app.inject({ method: 'GET', url: '/v1/dictations?limit=10', headers: { authorization: `Bearer ${token}` } })
  assert.equal(list.json().items.length, 1)
  assert.equal(list.json().items[0].injected, 'cleaned')
  await app.close()
})

test('validation: raw over 64KB is rejected, unknown mode is 400', async () => {
  const { app, post } = setup(good)
  const bad = await post('/v1/refine', body({ mode: 'email' }))
  assert.equal(bad.statusCode, 400)
  const huge = await post('/v1/refine', body({ raw: 'a'.repeat(70_000) }))
  assert.equal(huge.statusCode, 413)
  await app.close()
})

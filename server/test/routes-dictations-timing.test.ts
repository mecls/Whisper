import { test } from 'node:test'
import assert from 'node:assert/strict'
import { buildApp } from '../src/app.js'
import { openDb } from '../src/db/client.js'
import { createUser, issueToken } from '../src/auth.js'
import { dictations } from '../src/db/schema.js'
import type { LlmCaller } from '../src/llm/client.js'

/**
 * prd-sub-second-dictation.md rules 15-18: the dictations endpoint has to carry cleanup that
 * happened somewhere other than /v1/refine, and the release→paste measurement that the acceptance
 * criteria are evaluated against. Before this, the route wrote a literal null for cleaned, llmMs
 * and llmModel, so a locally-cleaned or gate-skipped dictation lost its text and all its timings.
 */

const raw = 'um so the the client wants to move the meeting to, uh, Thursday afternoon'
const CID = '22222222-2222-4222-8222-222222222222'
const body = (over: Record<string, unknown> = {}) => ({
  clientId: CID, raw, mode: 'clean', languageSetting: 'auto', languageDetected: 'en',
  context: { appBundleId: 'com.apple.mail', appName: 'Mail' }, timing: { audioMs: 6000, asrMs: 900 },
  asrModel: 'large-v3-turbo-632MB', clientVersion: '0.2.0', createdAt: 1757400000000,
  injected: 'cleaned', ...over,
})
const llm: LlmCaller = async () => ({ content: 'x', finishReason: 'stop', completionTokens: 1, reasoningChars: 0 })

function setup() {
  const db = openDb(':memory:')
  const u = createUser(db, 'Miguel')
  const token = issueToken(db, u.id, 'test')
  const app = buildApp({ db, llm, version: 'test' })
  const send = (url: string, payload: unknown, method: 'POST' | 'PATCH' = 'POST') =>
    app.inject({ method, url, payload, headers: { authorization: `Bearer ${token}` } })
  const row = () => db.select().from(dictations).all()[0]!
  return { db, app, send, row }
}

test('POST /v1/dictations persists client-side cleanup and its timings', async () => {
  const { app, send, row } = setup()
  const r = await send('/v1/dictations', body({
    cleaned: 'So the client wants to move the meeting to Thursday afternoon.',
    llmMs: 180, llmModel: 'apple-fm', totalMs: 740,
  }))
  assert.equal(r.statusCode, 200)
  assert.equal(row().cleaned, 'So the client wants to move the meeting to Thursday afternoon.')
  assert.equal(row().llmMs, 180)
  assert.equal(row().llmModel, 'apple-fm')
  assert.equal(row().totalMs, 740)
  await app.close()
})

test('the skip-gate path records llmModel "skipped" with no cleanup and still times the dictation', async () => {
  const { app, send, row } = setup()
  // Rule 14: a skipped dictation must still be logged, because the skip rate is the number that
  // says whether the gate's thresholds are right, and it is invisible unless it is recorded.
  const r = await send('/v1/dictations', body({ injected: 'raw', cleaned: null, llmModel: 'skipped', totalMs: 610 }))
  assert.equal(r.statusCode, 200)
  assert.equal(row().cleaned, null)
  assert.equal(row().llmModel, 'skipped')
  assert.equal(row().llmMs, null)
  assert.equal(row().totalMs, 610)
  assert.equal(row().injected, 'raw')
  await app.close()
})

test('PATCH by-client records totalMs alongside the injection outcome', async () => {
  const { app, send, row } = setup()
  await send('/v1/dictations', body({ injected: 'none' }))
  assert.equal(row().totalMs, null)
  const p = await send(`/v1/dictations/by-client/${CID}`, { injected: 'cleaned', totalMs: 820 }, 'PATCH')
  assert.equal(p.statusCode, 200)
  assert.equal(row().injected, 'cleaned')
  assert.equal(row().totalMs, 820)
  await app.close()
})

test('a PATCH without totalMs never blanks a measurement already stored', async () => {
  const { app, send, row } = setup()
  await send('/v1/dictations', body({ totalMs: 755 }))
  const p = await send(`/v1/dictations/by-client/${CID}`, { injected: 'raw' }, 'PATCH')
  assert.equal(p.statusCode, 200)
  assert.equal(row().injected, 'raw')
  assert.equal(row().totalMs, 755, 'an unmeasured PATCH must leave the stored measurement alone')
  await app.close()
})

test('an older client that sends none of the new fields still validates', async () => {
  // Rule 16: these are nullable with null defaults precisely so a client built before this change
  // does not start 400-ing, which would loop its dictations in the outbox forever.
  const { app, send, row } = setup()
  const r = await send('/v1/dictations', body({ injected: 'raw', fallbackReason: 'offline' }))
  assert.equal(r.statusCode, 200)
  assert.equal(row().cleaned, null)
  assert.equal(row().totalMs, null)
  assert.equal(row().fallbackReason, 'offline')
  await app.close()
})

test('a replay never erases a totalMs that an earlier call recorded', async () => {
  const { app, send, row } = setup()
  await send('/v1/dictations', body({ totalMs: 690, llmModel: 'gemma4' }))
  await send('/v1/dictations', body({ injected: 'raw', totalMs: null, llmModel: null }))
  assert.equal(row().totalMs, 690)
  assert.equal(row().llmModel, 'gemma4')
  await app.close()
})

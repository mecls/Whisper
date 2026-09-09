import { test } from 'node:test'
import assert from 'node:assert/strict'
import { buildApp } from '../src/app.js'
import { openDb } from '../src/db/client.js'
import { createUser, issueToken } from '../src/auth.js'

function setup() {
  const db = openDb(':memory:')
  const u = createUser(db, 'Miguel')
  const token = issueToken(db, u.id, 't')
  const other = createUser(db, 'Rita')
  const otherToken = issueToken(db, other.id, 't')
  const app = buildApp({ db, llm: async () => ({ content: '', finishReason: 'stop', completionTokens: 0, reasoningChars: 0 }), version: 'test' })
  const call = (method: 'GET' | 'POST' | 'PUT' | 'DELETE', url: string, payload?: unknown, tok = token) =>
    app.inject({ method, url, payload, headers: { authorization: `Bearer ${tok}` } })
  return { app, call, otherToken }
}

test('/v1/me returns user, default settings, dictionary and server info', async () => {
  const { app, call } = setup()
  const r = await call('GET', '/v1/me')
  assert.equal(r.statusCode, 200)
  const j = r.json()
  assert.equal(j.user.name, 'Miguel')
  assert.deepEqual(j.settings, { mode: 'clean', language: 'auto', hotkey: 'fn' })
  assert.deepEqual(j.dictionary, [])
  assert.equal(j.server.version, 'test')
  assert.equal(typeof j.server.concurrency, 'number')
  assert.deepEqual(j.server.allowedModels, ['gemma4', 'gpt-oss:120b', 'qwen3.5'])
  await app.close()
})

test('settings round-trip and reject unknown values', async () => {
  const { app, call } = setup()
  const put = await call('PUT', '/v1/settings', { mode: 'literal', language: 'pt', hotkey: 'rightOption', llmModel: 'gpt-oss:120b' })
  assert.equal(put.statusCode, 200)
  const get = await call('GET', '/v1/settings')
  assert.deepEqual(get.json(), { mode: 'literal', language: 'pt', hotkey: 'rightOption', llmModel: 'gpt-oss:120b' })
  const bad = await call('PUT', '/v1/settings', { mode: 'email', language: 'auto', hotkey: 'fn' })
  assert.equal(bad.statusCode, 400)
  await app.close()
})

// I5: settings.llmModel used to accept any string, letting a client silently pick a
// model the server was never configured to serve.
test('PUT /v1/settings rejects an llmModel outside LLM_ALLOWED_MODELS, accepts one inside it', async () => {
  const { app, call } = setup()
  const bad = await call('PUT', '/v1/settings', { mode: 'clean', language: 'auto', hotkey: 'fn', llmModel: 'nope' })
  assert.equal(bad.statusCode, 400)
  assert.equal(bad.json().error, 'unknown_model')

  const good = await call('PUT', '/v1/settings', { mode: 'clean', language: 'auto', hotkey: 'fn', llmModel: 'gpt-oss:120b' })
  assert.equal(good.statusCode, 200)
  assert.equal(good.json().llmModel, 'gpt-oss:120b')
  await app.close()
})

test('dictionary: team-wide entries are visible to everyone, personal ones only to their owner', async () => {
  const { app, call, otherToken } = setup()
  const a = await call('POST', '/v1/dictionary', { term: 'Miraside', teamWide: true })
  assert.equal(a.statusCode, 200)
  const b = await call('POST', '/v1/dictionary', { term: 'convex', replacement: 'Convex' })
  assert.equal(b.statusCode, 200)
  const mine = await call('GET', '/v1/dictionary')
  assert.equal(mine.json().length, 2)
  const theirs = await call('GET', '/v1/dictionary', undefined, otherToken)
  assert.equal(theirs.json().length, 1)
  const del = await call('DELETE', `/v1/dictionary/${b.json().id}`, undefined, otherToken)
  assert.equal(del.statusCode, 404) // not theirs
  const del2 = await call('DELETE', `/v1/dictionary/${b.json().id}`)
  assert.equal(del2.statusCode, 200)
  await app.close()
})

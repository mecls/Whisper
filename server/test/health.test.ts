import { test } from 'node:test'
import assert from 'node:assert/strict'
import { buildApp } from '../src/app.js'

test('GET /health is public and always 200', async () => {
  const app = buildApp({ db: null as never, llm: null as never, version: 'test' })
  const res = await app.inject({ method: 'GET', url: '/health' })
  assert.equal(res.statusCode, 200)
  const body = res.json()
  assert.equal(body.ok, true)
  assert.equal(body.version, 'test')
  assert.equal(body.db, 'ok')
  await app.close()
})

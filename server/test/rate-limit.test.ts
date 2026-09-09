import { test } from 'node:test'
import assert from 'node:assert/strict'
import { buildApp } from '../src/app.js'
import { openDb } from '../src/db/client.js'
import { createUser, issueToken } from '../src/auth.js'

// C1: @fastify/rate-limit is registered with `app.register` (deferred), and its
// `onRoute` hook only attaches the limiter to routes declared AFTER that hook exists.
// Declaring routes synchronously (not inside `app.after`) meant the limiter never
// attached to a single one of them — 130 authenticated requests in a minute produced
// zero 429s and no `x-ratelimit-limit` header.
test('the 120 req/min per-token rate limit attaches to a real route', async () => {
  const db = openDb(':memory:')
  const u = createUser(db, 'Miguel')
  const token = issueToken(db, u.id, 'x')
  const app = buildApp({ db, llm: null as never, version: 'test' })
  const inject = () => app.inject({ method: 'GET', url: '/v1/me', headers: { authorization: `Bearer ${token}` } })

  const first = await inject()
  assert.equal(first.statusCode, 200)
  assert.equal(first.headers['x-ratelimit-limit'], '120')

  let last
  for (let i = 0; i < 119; i++) last = await inject() // requests 2..120 in the window
  assert.equal(last!.statusCode, 200, 'request 120 should still be inside the window')

  const res121 = await inject()
  assert.equal(res121.statusCode, 429)
  assert.equal(res121.json().statusCode, 429)

  await app.close()
})

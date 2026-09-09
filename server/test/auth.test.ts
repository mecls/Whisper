import { test } from 'node:test'
import assert from 'node:assert/strict'
import { openDb } from '../src/db/client.js'
import { createUser, issueToken, revokeToken, authenticate, hashToken, generateToken, FailedAuthLimiter } from '../src/auth.js'
import { deviceTokens, users } from '../src/db/schema.js'
import { eq } from 'drizzle-orm'
import { buildApp } from '../src/app.js'

test('tokens are mv_ prefixed, 43 chars of base64url, stored only as sha256', () => {
  const { token, hash } = generateToken()
  assert.match(token, /^mv_[A-Za-z0-9_-]{43}$/)
  assert.equal(hash, hashToken(token))
  assert.match(hash, /^[0-9a-f]{64}$/)
})

test('authenticate resolves a live token and rejects everything else', () => {
  const db = openDb(':memory:')
  const u = createUser(db, 'Miguel')
  const token = issueToken(db, u.id, 'MacBook')
  const ok = authenticate(db, `Bearer ${token}`)
  assert.equal(ok.ok, true)
  if (ok.ok) assert.equal(ok.user.name, 'Miguel')

  assert.deepEqual(authenticate(db, undefined), { ok: false, error: 'missing_token' })
  assert.deepEqual(authenticate(db, 'Bearer mv_nope'), { ok: false, error: 'invalid_token' })
  assert.deepEqual(authenticate(db, 'Basic abc'), { ok: false, error: 'missing_token' })

  const row = db.select().from(deviceTokens).where(eq(deviceTokens.userId, u.id)).get()!
  revokeToken(db, row.id)
  assert.deepEqual(authenticate(db, `Bearer ${token}`), { ok: false, error: 'token_revoked' })

  const t2 = issueToken(db, u.id, 'second')
  db.update(users).set({ disabledAt: 1 }).where(eq(users.id, u.id)).run()
  assert.deepEqual(authenticate(db, `Bearer ${t2}`), { ok: false, error: 'user_disabled' })
})

test('last_used_at is written at most once per 5 minutes', () => {
  const db = openDb(':memory:')
  const u = createUser(db, 'A')
  const token = issueToken(db, u.id, 'x')
  authenticate(db, `Bearer ${token}`, 1_000_000)
  authenticate(db, `Bearer ${token}`, 1_000_000 + 60_000)
  const row = db.select().from(deviceTokens).where(eq(deviceTokens.userId, u.id)).get()!
  assert.equal(row.lastUsedAt, 1_000_000)
  authenticate(db, `Bearer ${token}`, 1_000_000 + 6 * 60_000)
  const row2 = db.select().from(deviceTokens).where(eq(deviceTokens.userId, u.id)).get()!
  assert.equal(row2.lastUsedAt, 1_000_000 + 6 * 60_000)
})

test('FailedAuthLimiter blocks after the failure ceiling and resets when the window expires', () => {
  const limiter = new FailedAuthLimiter(3, 1000)
  const ip = '1.2.3.4'
  for (let i = 0; i < 3; i++) {
    assert.equal(limiter.shouldBlock(ip, 0), false)
    limiter.record(ip, 0)
  }
  assert.equal(limiter.shouldBlock(ip, 0), true)
  // a different IP is unaffected
  assert.equal(limiter.shouldBlock('9.9.9.9', 0), false)
  // the window has expired: the counter resets
  assert.equal(limiter.shouldBlock(ip, 1000), false)
})

test('a scanner sending Bearer mv_invalid is 429-rate-limited after 30 failed authentications', async () => {
  const app = buildApp({ db: openDb(':memory:'), llm: null as never, version: 'test' })
  const responses = []
  for (let i = 0; i < 31; i++) {
    responses.push(await app.inject({ method: 'GET', url: '/v1/me', headers: { authorization: 'Bearer mv_invalid' } }))
  }
  for (const res of responses.slice(0, 30)) assert.equal(res.statusCode, 401)
  assert.equal(responses[30]!.statusCode, 429)
  assert.deepEqual(responses[30]!.json(), { error: 'rate_limited', message: 'Too many failed authentications' })
  await app.close()
})

import { test } from 'node:test'
import assert from 'node:assert/strict'
import { buildApp } from '../src/app.js'
import { openDb } from '../src/db/client.js'
import { createUser, issueToken } from '../src/auth.js'

// C3: the restore runbook only works if a graceful stop checkpoints the WAL back into
// the main db file, which requires the sqlite connection to actually be closed — a bare
// `app.close()` without closing the driver leaves the WAL file behind.
test('app.close() also closes the underlying sqlite connection (WAL checkpoint on shutdown)', async () => {
  const db = openDb(':memory:')
  const app = buildApp({ db, llm: null as never, version: 'test' })
  db.raw.prepare('select 1').get() // sanity: usable before close
  await app.close()
  assert.throws(() => db.raw.prepare('select 1').get(), /database connection is not open/i)
})

// I4: a thrown error must never leak internal detail (stack traces, driver text,
// prompt/transcript content that might be in scope) to the client.
test('an unhandled error in a route becomes a generic 500 with no internal detail', async () => {
  const db = openDb(':memory:')
  const u = createUser(db, 'Miguel')
  const token = issueToken(db, u.id, 'x')
  const app = buildApp({ db, llm: null as never, version: 'test' })
  app.get('/__boom', async () => {
    throw new Error('sensitive detail: sk-fake-secret-should-never-leak')
  })

  const res = await app.inject({ method: 'GET', url: '/__boom', headers: { authorization: `Bearer ${token}` } })
  assert.equal(res.statusCode, 500)
  assert.deepEqual(res.json(), { error: 'server', message: 'Internal error' })
  assert.ok(!res.body.includes('sensitive detail'))

  await app.close()
})

// I4: existing 4xx behaviour (zod validation) must be unaffected by the new error handler.
test('validation errors keep their existing 400 shape after the error handler change', async () => {
  const db = openDb(':memory:')
  const u = createUser(db, 'Miguel')
  const token = issueToken(db, u.id, 'x')
  const app = buildApp({ db, llm: null as never, version: 'test' })
  const res = await app.inject({
    method: 'PUT',
    url: '/v1/settings',
    headers: { authorization: `Bearer ${token}` },
    payload: { mode: 'email', language: 'auto', hotkey: 'fn' },
  })
  assert.equal(res.statusCode, 400)
  assert.equal(res.json().code, 'FST_ERR_VALIDATION')
  await app.close()
})

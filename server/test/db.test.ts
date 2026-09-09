import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { eq } from 'drizzle-orm'
import { openDb } from '../src/db/client.js'
import { users, dictations } from '../src/db/schema.js'

test('migrations apply and dictations enforce (user, client_id) uniqueness', () => {
  const db = openDb(':memory:')
  const tables = db.raw.prepare("select name from sqlite_master where type='table' order by name").all() as { name: string }[]
  assert.deepEqual(
    tables.map((t) => t.name).filter((n) => !n.startsWith('sqlite_')),
    ['_migrations', 'device_tokens', 'dictations', 'dictionary_entries', 'user_settings', 'users'],
  )
  db.insert(users).values({ id: 'u1', name: 'Miguel', createdAt: 1 }).run()
  const row = { id: 'd1', userId: 'u1', clientId: 'c1', createdAt: 1, raw: 'olá', mode: 'clean', languageSetting: 'auto' }
  db.insert(dictations).values(row).run()
  assert.throws(() => db.insert(dictations).values({ ...row, id: 'd2' }).run(), /UNIQUE/)
  const got = db.select().from(dictations).where(eq(dictations.clientId, 'c1')).get()
  assert.equal(got?.injected, null)
  assert.equal(got?.cleaned, null)
})

test('opening twice does not re-run migrations', () => {
  const dir = mkdtempSync(join(tmpdir(), 'voice-db-'))
  const dbPath = join(dir, 'voice.db')
  try {
    const first = openDb(dbPath)
    first.raw.close()

    const second = openDb(dbPath)
    const n = second.raw.prepare('select count(*) as n from _migrations').get() as { n: number }
    assert.equal(n.n, 1)
    const tables = second.raw.prepare("select name from sqlite_master where type='table'").all() as { name: string }[]
    assert.ok(tables.some((t) => t.name === 'dictations'))
    second.raw.close()
  } finally {
    rmSync(dir, { recursive: true, force: true })
  }
})

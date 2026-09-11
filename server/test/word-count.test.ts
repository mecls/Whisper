import { test } from 'node:test'
import assert from 'node:assert/strict'
import { buildApp } from '../src/app.js'
import { openDb, type Db } from '../src/db/client.js'
import { createUser } from '../src/auth.js'
import { dictations } from '../src/db/schema.js'
import { backfillWordCounts, countWords, upsertDictation, type DictationUpsert } from '../src/dictations-repo.js'
import type { LlmCaller } from '../src/llm/client.js'

/**
 * prd-insights-dashboard.md rules 5-7. `word_count` is the only number on the dashboard that is
 * written rather than derived at read time, so nothing downstream can correct it — every one of
 * these tests is guarding a value that would otherwise be wrong forever.
 */

const llm: LlmCaller = async () => ({ content: 'x', finishReason: 'stop', completionTokens: 1, reasoningChars: 0 })

const RAW_10 = 'um so the the client wants to move the meeting'             // 10 words
const CLEANED_6 = 'The client wants to move the'                            // 6 words

function base(over: Partial<DictationUpsert> = {}): Omit<DictationUpsert, 'userId'> {
  return {
    clientId: 'c1', createdAt: 1757400000000, raw: RAW_10, cleaned: null, injected: 'raw',
    mode: 'clean', languageSetting: 'auto', languageDetected: 'en',
    appBundleId: 'com.apple.mail', appName: 'Mail', audioMs: 6000, asrMs: 900,
    llmMs: null, totalMs: null, asrModel: 'large-v3-turbo', llmModel: null,
    clientVersion: '0.2.0', fallbackReason: null, ...over,
  }
}

function setup(): { db: Db; userId: string; count: () => number | null } {
  const db = openDb(':memory:')
  const userId = createUser(db, 'Miguel').id
  const count = () => db.select({ w: dictations.wordCount }).from(dictations).all()[0]!.w
  return { db, userId, count }
}

test('countWords is whitespace-separated tokens after trimming', () => {
  assert.equal(countWords('one two three'), 3)
  assert.equal(countWords('  leading and trailing  '), 3)
  assert.equal(countWords('collapses\t multiple\n\n whitespace'), 3)
  assert.equal(countWords(''), 0)
  assert.equal(countWords('   '), 0, 'whitespace only is zero words, never one')
  assert.equal(countWords('word'), 1)
  // Punctuation rides along with its token: "€25" and "well-formed" are one word each, which is
  // what a person counting a sentence by eye would say.
  assert.equal(countWords('vinte e cinco euros, às três e meia.'), 8)
})

test('a first insert counts the cleaned text when there is one, the raw text otherwise', () => {
  const { db, userId, count } = setup()
  upsertDictation(db, { userId, ...base() })
  assert.equal(count(), 10, 'no cleanup yet, so the raw transcript is what got pasted')

  const other = openDb(':memory:')
  const u2 = createUser(other, 'Miguel').id
  upsertDictation(other, { userId: u2, ...base({ cleaned: CLEANED_6, injected: 'cleaned' }) })
  assert.equal(other.select({ w: dictations.wordCount }).from(dictations).all()[0]!.w, 6)
})

test('a later cleanup updates the count to the cleaned text', () => {
  const { db, userId, count } = setup()
  upsertDictation(db, { userId, ...base() })
  assert.equal(count(), 10)
  upsertDictation(db, { userId, ...base({ cleaned: CLEANED_6, injected: 'cleaned' }) })
  assert.equal(count(), 6, 'the cleanup removed four filler words and the count must follow')
})

test('a replay carrying cleaned:null never downgrades a count back to the raw text', () => {
  // The ELSE branch of the CASE. Without it an outbox replay — which legitimately carries
  // cleaned:null, because the client never saw the cleanup — would restore the raw count and
  // silently inflate the word total by every filler the cleanup had removed.
  const { db, userId, count } = setup()
  upsertDictation(db, { userId, ...base({ cleaned: CLEANED_6, injected: 'cleaned' }) })
  assert.equal(count(), 6)
  upsertDictation(db, { userId, ...base({ cleaned: null, injected: 'raw' }) })
  assert.equal(count(), 6, 'a replay with no cleanup must leave the cleaned count alone')
})

test('the count is written by the same statement as the row, with no read-back', () => {
  // Rule 6 is a performance and correctness claim at once: one statement means there is no window
  // in which a concurrent replay can observe a row whose text and count disagree.
  const { db, userId } = setup()
  let statements = 0
  const raw = db.raw
  const originalPrepare = raw.prepare.bind(raw)
  ;(raw as unknown as { prepare: typeof raw.prepare }).prepare = ((sql: string) => {
    if (/insert into .dictations./i.test(sql) || /update .dictations./i.test(sql)) statements++
    return originalPrepare(sql)
  }) as typeof raw.prepare
  upsertDictation(db, { userId, ...base() })
  assert.equal(statements, 1, 'the upsert must be a single write statement')
})

test('backfillWordCounts fills only NULL rows, prefers cleaned, and is idempotent', () => {
  const { db, userId } = setup()
  // Simulate rows written before the column existed: the repo can no longer produce a NULL count.
  const insert = db.raw.prepare(
    `insert into dictations (id, user_id, client_id, created_at, raw, cleaned, mode, language_setting, word_count)
     values (?, ?, ?, ?, ?, ?, 'clean', 'auto', ?)`,
  )
  insert.run('a', userId, 'ca', 1, RAW_10, null, null)
  insert.run('b', userId, 'cb', 2, RAW_10, CLEANED_6, null)
  insert.run('c', userId, 'cc', 3, RAW_10, CLEANED_6, 99) // already counted; must not be touched

  assert.equal(backfillWordCounts(db), 2, 'only the two NULL rows are candidates')
  const rows = Object.fromEntries(
    db.select({ id: dictations.id, w: dictations.wordCount }).from(dictations).all().map((r) => [r.id, r.w]),
  )
  assert.equal(rows.a, 10)
  assert.equal(rows.b, 6, 'the backfill counts what was pasted, so cleaned wins over raw')
  assert.equal(rows.c, 99, 'a row that already has a count is left exactly as it was')

  assert.equal(backfillWordCounts(db), 0, 'running it again is a no-op')
})

test('backfillWordCounts chunks without losing rows', () => {
  const { db, userId } = setup()
  const insert = db.raw.prepare(
    `insert into dictations (id, user_id, client_id, created_at, raw, mode, language_setting, word_count)
     values (?, ?, ?, ?, ?, 'clean', 'auto', null)`,
  )
  for (let i = 0; i < 25; i++) insert.run(`id${i}`, userId, `cid${i}`, i + 1, RAW_10)
  assert.equal(backfillWordCounts(db, 4), 25, 'a chunk size that does not divide the row count must still finish')
  const remaining = db.raw.prepare('select count(*) as n from dictations where word_count is null').get() as { n: number }
  assert.equal(remaining.n, 0)
})

test('booting the app backfills existing NULL counts and never throws on failure', async () => {
  const { db, userId } = setup()
  db.raw.prepare(
    `insert into dictations (id, user_id, client_id, created_at, raw, mode, language_setting, word_count)
     values ('a', ?, 'ca', 1, ?, 'clean', 'auto', null)`,
  ).run(userId, RAW_10)

  const app = buildApp({ db, llm, version: 'test' })
  const row = db.select({ w: dictations.wordCount }).from(dictations).all()[0]!
  assert.equal(row.w, 10, 'boot must leave no uncounted row behind')
  await app.close()
})

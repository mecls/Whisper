import { test } from 'node:test'
import assert from 'node:assert/strict'
import { buildApp } from '../src/app.js'
import { openDb, type Db } from '../src/db/client.js'
import { createUser, issueToken } from '../src/auth.js'
import { upsertDictation, type DictationUpsert } from '../src/dictations-repo.js'
import {
  allocateShares, computeStreak, dayNumber, heatmapWindow, localDayKey, APP_ROWS, WPM_MIN_AUDIO_MS,
} from '../src/routes/insights.js'
import type { LlmCaller } from '../src/llm/client.js'

/**
 * prd-insights-dashboard.md rules 6-20, and the acceptance scenarios in
 * tasks/insights-dashboard-build-spec.md §13. AC-4 is the one that matters most: it asserts
 * against the serialized response rather than trusting that the handler's queries stay clean.
 */

const MS_PER_DAY = 86_400_000
const TZ = 'Europe/Lisbon'
const llm: LlmCaller = async () => ({ content: 'x', finishReason: 'stop', completionTokens: 1, reasoningChars: 0 })

const APPS = ['Slack', 'Mail', 'Notes', 'Safari', 'Messages', 'Cursor', 'Linear', 'Notion', 'Terminal']
const TEN_WORDS = 'um so the the client wants to move the meeting'

/**
 * Midday UTC on the calendar day `offset` days before today in `tz`. Midday, not midnight, so a
 * one-hour zone difference can never slide a fixture into the adjacent day and make the test
 * depend on where the machine running it happens to be.
 */
function atDay(offset: number, tz = TZ): number {
  const today = dayNumber(localDayKey(Date.now(), tz))
  return (today - offset) * MS_PER_DAY + 12 * 3_600_000
}

function dictation(over: Partial<DictationUpsert> & { userId: string; clientId: string }): DictationUpsert {
  return {
    createdAt: atDay(0), raw: TEN_WORDS, cleaned: null, injected: 'raw', mode: 'clean',
    languageSetting: 'auto', languageDetected: 'en', appBundleId: 'com.apple.mail', appName: 'Mail',
    audioMs: 4000, asrMs: 400, llmMs: null, totalMs: 900, asrModel: 'large-v3-turbo',
    llmModel: null, clientVersion: '0.2.0', fallbackReason: null, ...over,
  }
}

function setup() {
  const db = openDb(':memory:')
  const a = createUser(db, 'A')
  const b = createUser(db, 'B')
  const tokenA = issueToken(db, a.id, 'test')
  const tokenB = issueToken(db, b.id, 'test')
  const app = buildApp({ db, llm, version: 'test' })
  const get = (qs = '', token = tokenA) =>
    app.inject({ method: 'GET', url: `/v1/insights${qs}`, headers: { authorization: `Bearer ${token}` } })
  return { db, app, a, b, tokenA, tokenB, get }
}

// ---------------------------------------------------------------- pure aggregation

test('computeStreak counts back from today, or from yesterday when today is empty', () => {
  const today = 20_000
  assert.deepEqual(computeStreak(new Set([today, today - 1, today - 2]), today), { current: 3, longest: 3 })
  // Rule 17: without the yesterday fallback the streak reads 0 every morning until the first
  // dictation of the day — wrong, and discouraging exactly when the number should encourage.
  assert.equal(computeStreak(new Set([today - 1, today - 2]), today).current, 2)
  assert.equal(computeStreak(new Set([today - 2, today - 3]), today).current, 0, 'a gap at yesterday ends it')
  assert.equal(computeStreak(new Set(), today).current, 0)
})

test('computeStreak reports the longest run independently of the current one', () => {
  const today = 20_000
  const days = new Set([today, today - 1, today - 10, today - 11, today - 12, today - 13])
  assert.deepEqual(computeStreak(days, today), { current: 2, longest: 4 })
})

test('allocateShares always sums to exactly 100', () => {
  for (const counts of [[1, 1, 1], [7, 3], [1, 1, 1, 1, 1, 1, 1], [100], [5, 5, 5, 5, 5, 5, 70], [2, 3, 5, 7, 11, 13, 17]]) {
    const shares = allocateShares(counts)
    assert.equal(shares.reduce((x, y) => x + y, 0), 100, `${counts} -> ${shares}`)
  }
  assert.deepEqual(allocateShares([0, 0]), [0, 0], 'no dictations means no shares, not a divide by zero')
})

test('heatmapWindow covers whole Sun-Sat weeks plus the current partial week', () => {
  // 2026-09-12 is a Saturday, so the current week contributes all 7 of its days.
  const sat = dayNumber('2026-09-12')
  assert.equal(new Date(sat * MS_PER_DAY).getUTCDay(), 6)
  const w = heatmapWindow(sat, 3)
  assert.equal(w.to, sat)
  assert.equal(w.to - w.from + 1, 3 * 7 + 7)
  assert.equal(new Date(w.from * MS_PER_DAY).getUTCDay(), 0, 'the window always begins on a Sunday')

  const wed = dayNumber('2026-09-09')
  assert.equal(heatmapWindow(wed, 3).to - heatmapWindow(wed, 3).from + 1, 3 * 7 + 4)
})

test('day bucketing follows the zone across a DST change', () => {
  // 2026-03-29 01:30 UTC is still 2026-03-29 in Lisbon (clocks go forward at 01:00 to 02:00),
  // but it is already 2026-03-29 02:30 in Madrid. The point is that neither is a fixed offset
  // from UTC year-round, which is why SQLite cannot do this (rule 8).
  assert.equal(localDayKey(Date.UTC(2026, 2, 29, 1, 30), 'Europe/Lisbon'), '2026-03-29')
  assert.equal(localDayKey(Date.UTC(2026, 2, 29, 23, 30), 'Europe/Lisbon'), '2026-03-30')
  assert.equal(localDayKey(Date.UTC(2026, 2, 29, 23, 30), 'Pacific/Auckland'), '2026-03-30')
  assert.equal(localDayKey(Date.UTC(2026, 2, 29, 23, 30), 'America/Los_Angeles'), '2026-03-29')
  // Consecutive local days stay adjacent integers across the change, or the streak would break.
  assert.equal(dayNumber('2026-03-30') - dayNumber('2026-03-29'), 1)
})

// ---------------------------------------------------------------- AC-1

test('AC-1: a populated dashboard reports every card correctly', async () => {
  const { db, app, a, get } = setup()

  // A 5-day run ending today, then a deliberate hole at day 5 so the streak is exactly 5 and not
  // an accident of the fixture running to the edge of the range.
  const plan: number[] = []
  for (let d = 0; d < 5; d++) for (let i = 0; i < 4; i++) plan.push(d)
  const older: number[] = []
  for (let d = 6; d <= 139; d++) if (d % 3 !== 0) older.push(d)
  for (let i = 0; plan.length < 200; i++) plan.push(older[i % older.length]!)

  for (const [i, offset] of plan.entries()) {
    upsertDictation(db, dictation({
      userId: a.id, clientId: `a${i}`, createdAt: atDay(offset),
      appName: APPS[i % APPS.length]!, appBundleId: `bundle.${i % APPS.length}`,
    }))
  }

  const r = await get(`?tz=${TZ}&weeks=21`)
  assert.equal(r.statusCode, 200)
  const body = r.json()

  const expectedWords = (db.raw.prepare('select sum(word_count) as n from dictations').get() as { n: number }).n
  assert.equal(body.totals.dictations, 200)
  assert.equal(body.totals.words, expectedWords)
  assert.equal(body.totals.words, 200 * 10)
  assert.equal(Number.isInteger(body.totals.wpm), true, 'wpm is a whole number')
  assert.equal(body.totals.wpm, 150, '10 words in 4000 ms is 150 wpm')

  assert.equal(body.streak.current, 5)

  assert.equal(body.apps.length, APP_ROWS + 1, 'six named apps plus Other')
  assert.equal(body.apps.at(-1).label, 'Other')
  assert.equal(body.apps.reduce((s: number, x: { share: number }) => s + x.share, 0), 100)
  assert.equal(body.apps.reduce((s: number, x: { dictations: number }) => s + x.dictations, 0), 200)

  // Rule 20: every day in the window is present, including the empty ones.
  assert.equal(body.days.length, 21 * 7 + new Date(dayNumber(localDayKey(Date.now(), TZ)) * MS_PER_DAY).getUTCDay() + 1)
  for (let i = 1; i < body.days.length; i++) {
    assert.equal(dayNumber(body.days[i].date) - dayNumber(body.days[i - 1].date), 1, 'no gaps in the day list')
  }
  assert.ok(body.days.some((d: { dictations: number }) => d.dictations === 0), 'quiet days are zero-filled, not omitted')
  assert.equal(body.days.at(-1).date, localDayKey(Date.now(), TZ), 'the window ends today')
  await app.close()
})

// ---------------------------------------------------------------- AC-2

test('AC-2: an unknown time zone is a 400, never a silent fallback to UTC', async () => {
  const { app, get } = setup()
  const r = await get('?tz=Not/AZone')
  assert.equal(r.statusCode, 400)
  assert.deepEqual(Object.keys(r.json()).sort(), ['error', 'message'])
  assert.equal(r.json().error, 'bad_request')
  await app.close()
})

test('AC-2: weeks outside 1-53 is rejected rather than scanning unbounded history', async () => {
  const { app, get } = setup()
  assert.equal((await get(`?tz=${TZ}&weeks=0`)).statusCode, 400)
  assert.equal((await get(`?tz=${TZ}&weeks=54`)).statusCode, 400)
  assert.equal((await get(`?tz=${TZ}&weeks=53`)).statusCode, 200)
  await app.close()
})

test('AC-2: no Authorization header is a 401 and never touches the dictations table', async () => {
  const { db, app } = setup()
  let dictationQueries = 0
  const raw = db.raw
  const original = raw.prepare.bind(raw)
  ;(raw as unknown as { prepare: typeof raw.prepare }).prepare = ((s: string) => {
    if (/\bdictations\b/i.test(s)) dictationQueries++
    return original(s)
  }) as typeof raw.prepare

  const r = await app.inject({ method: 'GET', url: '/v1/insights' })
  assert.equal(r.statusCode, 401)
  assert.equal(dictationQueries, 0, 'authentication must reject before any aggregate runs')
  await app.close()
})

// ---------------------------------------------------------------- AC-3

test('AC-3: one user never sees another user\'s numbers', async () => {
  const { db, app, a, b, tokenB, get } = setup()
  for (let i = 0; i < 10; i++) {
    upsertDictation(db, dictation({ userId: a.id, clientId: `a${i}`, appName: 'Slack' }))
    upsertDictation(db, dictation({ userId: b.id, clientId: `b${i}`, appName: 'Xcode', createdAt: atDay(1) }))
  }
  const forA = (await get(`?tz=${TZ}`)).json()
  assert.equal(forA.totals.dictations, 10, 'A sees 10, not 20')
  assert.equal(forA.totals.words, 100)
  assert.equal(forA.apps.length, 1)
  assert.equal(forA.apps[0].label, 'Slack')
  assert.ok(!forA.apps.some((x: { label: string }) => x.label === 'Xcode'), "B's app must not appear")
  assert.equal(forA.days.at(-1).dictations, 10, "only A's dictations land in A's day buckets")
  assert.equal(forA.days.at(-2).dictations, 0, "B's day is empty for A")

  const forB = (await get(`?tz=${TZ}`, tokenB)).json()
  assert.equal(forB.totals.dictations, 10)
  assert.equal(forB.apps[0].label, 'Xcode')
  await app.close()
})

// ---------------------------------------------------------------- AC-4

test('AC-4: no transcript text escapes, at any depth', async () => {
  const { db, app, a, get } = setup()
  upsertDictation(db, dictation({
    userId: a.id, clientId: 'canary',
    raw: 'ZZQX-CANARY-TRANSCRIPT-ZZQX', cleaned: 'ZZQX-CANARY-CLEANED-ZZQX', injected: 'cleaned',
  }))
  const r = await get(`?tz=${TZ}`)
  assert.equal(r.statusCode, 200)

  assert.equal(r.payload.includes('ZZQX-CANARY-TRANSCRIPT-ZZQX'), false)
  assert.equal(r.payload.includes('ZZQX-CANARY-CLEANED-ZZQX'), false)
  assert.equal(r.payload.includes('ZZQX'), false)

  const keys = new Set<string>()
  const walk = (v: unknown): void => {
    if (Array.isArray(v)) return v.forEach(walk)
    if (v && typeof v === 'object') {
      for (const [k, val] of Object.entries(v)) { keys.add(k); walk(val) }
    }
  }
  walk(r.json())
  assert.equal(keys.has('raw'), false, 'no key named raw at any depth')
  assert.equal(keys.has('cleaned'), false, 'no key named cleaned at any depth')
  // The row is counted even though its text never appears — the numbers are real, the text is not.
  assert.equal(r.json().totals.dictations, 1)
  await app.close()
})

test('AC-4: the handler never issues a query that selects raw or cleaned', async () => {
  const { db, app, a, get } = setup()
  upsertDictation(db, dictation({ userId: a.id, clientId: 'x' }))
  const raw = db.raw
  const original = raw.prepare.bind(raw)
  const seen: string[] = []
  ;(raw as unknown as { prepare: typeof raw.prepare }).prepare = ((s: string) => { seen.push(s); return original(s) }) as typeof raw.prepare

  await get(`?tz=${TZ}`)
  const offenders = seen.filter((s) => /select[\s\S]*?\b(raw|cleaned)\b[\s\S]*?from\s+"?dictations"?/i.test(s))
  assert.deepEqual(offenders, [], 'rule 11: a handler that never loads a transcript cannot leak one')
  await app.close()
})

// ---------------------------------------------------------------- AC-5

test('AC-5: an outbox replay never downgrades a word count', async () => {
  const { db, app, a, tokenA } = setup()
  upsertDictation(db, dictation({
    userId: a.id, clientId: 'replay', raw: TEN_WORDS, cleaned: 'The client wants to move the', injected: 'cleaned',
  }))
  const before = (db.raw.prepare('select word_count as w from dictations').get() as { w: number }).w
  assert.equal(before, 6)

  const r = await app.inject({
    method: 'POST', url: '/v1/dictations', headers: { authorization: `Bearer ${tokenA}` },
    payload: {
      clientId: 'replay', raw: TEN_WORDS, cleaned: null, mode: 'clean', languageSetting: 'auto',
      context: { appName: 'Mail' }, timing: { audioMs: 4000, asrMs: 400 },
      asrModel: 'large-v3-turbo', clientVersion: '0.2.0', createdAt: atDay(0), injected: 'raw',
    },
  })
  assert.equal(r.statusCode, 200)
  const after = (db.raw.prepare('select word_count as w from dictations').get() as { w: number }).w
  assert.equal(after, 6, 'a replay with no cleanup must not restore the raw count')
  await app.close()
})

// ---------------------------------------------------------------- AC-6

test('AC-6: a streak longer than the window still reads its true length', async () => {
  const { db, app, a, get } = setup()
  for (let d = 0; d < 30; d++) {
    upsertDictation(db, dictation({ userId: a.id, clientId: `d${d}`, createdAt: atDay(d) }))
  }
  const body = (await get(`?tz=${TZ}&weeks=1`)).json()
  assert.equal(body.streak.current, 30, 'rule 13: the streak is computed over all history')
  assert.equal(body.streak.longest, 30)
  assert.ok(body.days.length <= 14, `a one-week window is at most 14 days, got ${body.days.length}`)
  assert.ok(body.days.length < 30, 'the heatmap is windowed even though the streak is not')
  await app.close()
})

// ---------------------------------------------------------------- remaining numbered rules

test('an un-backfilled word_count is excluded from the total, never summed as zero', async () => {
  const { db, app, a, get } = setup()
  upsertDictation(db, dictation({ userId: a.id, clientId: 'counted' }))
  db.raw.prepare(
    `insert into dictations (id, user_id, client_id, created_at, raw, mode, language_setting, audio_ms, word_count)
     values ('legacy', ?, 'legacy', ?, ?, 'clean', 'auto', 4000, null)`,
  ).run(a.id, atDay(0), TEN_WORDS)

  const body = (await get(`?tz=${TZ}`)).json()
  assert.equal(body.totals.dictations, 2, 'an uncounted row is still a dictation')
  assert.equal(body.totals.words, 10, 'but it contributes no words until it is backfilled')
  assert.equal(body.days.at(-1).dictations, 2)
  assert.equal(body.days.at(-1).words, 10)
  await app.close()
})

test('wpm is null below a minute of audio, and ignores dictations with no audio', async () => {
  const { db, app, a, get } = setup()
  upsertDictation(db, dictation({ userId: a.id, clientId: 'short', audioMs: 3000 }))
  assert.equal((await get(`?tz=${TZ}`)).json().totals.wpm, null, `under ${WPM_MIN_AUDIO_MS} ms this is arithmetic, not information`)

  // 21 dictations x 10 words: 20 of 4 s plus the 3 s one above = 210 words over 83 s -> 152 wpm.
  for (let i = 0; i < 20; i++) upsertDictation(db, dictation({ userId: a.id, clientId: `w${i}`, audioMs: 4000 }))
  // A row with no audio contributes its words to the total but neither term of the rate.
  upsertDictation(db, dictation({ userId: a.id, clientId: 'noaudio', audioMs: null }))
  const body = (await get(`?tz=${TZ}`)).json()
  assert.equal(body.totals.wpm, 152)
  assert.equal(body.totals.words, 220, 'the audio-less dictation still counts toward total words')
  assert.equal(body.totals.audioMs, 20 * 4000 + 3000)
  await app.close()
})

test('app labels fall back from name to bundle id to Unknown, and merge by label', async () => {
  const { db, app, a, get } = setup()
  upsertDictation(db, dictation({ userId: a.id, clientId: '1', appName: 'Slack', appBundleId: 'com.tinyspeck' }))
  // Same app, bundle id changed by an update: one label, not two rows.
  upsertDictation(db, dictation({ userId: a.id, clientId: '2', appName: 'Slack', appBundleId: 'com.slack.new' }))
  upsertDictation(db, dictation({ userId: a.id, clientId: '3', appName: null, appBundleId: 'com.unknown.app' }))
  upsertDictation(db, dictation({ userId: a.id, clientId: '4', appName: null, appBundleId: null }))

  const apps = (await get(`?tz=${TZ}`)).json().apps as { label: string; dictations: number; share: number }[]
  assert.equal(apps.length, 3, 'no Other row when nothing overflows into it')
  const byLabel = Object.fromEntries(apps.map((x) => [x.label, x.dictations]))
  assert.equal(byLabel.Slack, 2)
  assert.equal(byLabel['com.unknown.app'], 1)
  assert.equal(byLabel.Unknown, 1)
  assert.equal(apps.reduce((s, x) => s + x.share, 0), 100)
  await app.close()
})

test('a user with no dictations gets a well-formed empty response, not an error', async () => {
  const { app, get } = setup()
  const r = await get(`?tz=${TZ}`)
  assert.equal(r.statusCode, 200)
  const body = r.json()
  assert.deepEqual(body.totals, { dictations: 0, words: 0, audioMs: 0, wpm: null })
  assert.deepEqual(body.streak, { current: 0, longest: 0 })
  assert.deepEqual(body.apps, [])
  assert.ok(body.days.length > 0, 'the grid is still drawn, every cell empty')
  assert.ok(body.days.every((d: { dictations: number }) => d.dictations === 0))
  await app.close()
})

test('zones that Intl can format are accepted, including UTC and aliases', async () => {
  // `Intl.supportedValuesOf('timeZone')` lists only canonical zones — 418 of them here, with UTC,
  // every Etc/* and every alias missing. Validating against that list would 400 a Mac whose
  // TimeZone.current reports any of these, so the formatter is the validator instead.
  const { app, get } = setup()
  for (const tz of ['UTC', 'Etc/UTC', 'Asia/Calcutta', 'America/Buenos_Aires', 'Europe/Lisbon', 'Pacific/Auckland']) {
    assert.equal((await get(`?tz=${encodeURIComponent(tz)}`)).statusCode, 200, `${tz} must be accepted`)
  }
  for (const tz of ['Not/AZone', 'Europe/Lisbo', 'nonsense']) {
    assert.equal((await get(`?tz=${encodeURIComponent(tz)}`)).statusCode, 400, `${tz} must be rejected`)
  }
  await app.close()
})

test('tz defaults to UTC when absent, which is a real zone rather than a guess', async () => {
  const { app, get } = setup()
  const r = await get('')
  assert.equal(r.statusCode, 200)
  assert.equal(r.json().days.at(-1).date, localDayKey(Date.now(), 'UTC'))
  await app.close()
})

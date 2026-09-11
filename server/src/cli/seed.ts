import { isAbsolute, relative, resolve } from 'node:path'
import { eq } from 'drizzle-orm'
import { openDb } from '../db/client.js'
import { env } from '../env.js'
import { createUser, issueToken } from '../auth.js'
import { deviceTokens, users } from '../db/schema.js'
import { upsertDictation } from '../dictations-repo.js'
import { BENCH_FIXTURES } from './bench-fixtures.js'

/**
 * Synthetic dictation history, so the Insights dashboard can be developed and reviewed without a
 * reachable server and without anyone having to talk at their Mac for five months.
 *
 * `npm run seed -- <user name> [--dictations 200] [--weeks 20] [--streak 12]`
 *
 * Idempotent by user: every generated row has a deterministic client id (`seed-<n>`) and goes
 * through the ordinary upsert, so a second run rewrites the same rows instead of doubling the
 * history. It adds no delete path — re-running with a *smaller* `--dictations` leaves the surplus
 * rows from the larger run in place, which the summary line reports honestly.
 */

const APPS: { name: string; bundleId: string }[] = [
  { name: 'Slack', bundleId: 'com.tinyspeck.slackmacgap' },
  { name: 'Mail', bundleId: 'com.apple.mail' },
  { name: 'Notes', bundleId: 'com.apple.Notes' },
  { name: 'Safari', bundleId: 'com.apple.Safari' },
  { name: 'Messages', bundleId: 'com.apple.MobileSMS' },
  { name: 'Cursor', bundleId: 'com.todesktop.230313mzl4w4u92' },
  { name: 'Linear', bundleId: 'com.linear' },
  { name: 'Notion', bundleId: 'notion.id' },
  { name: 'Terminal', bundleId: 'com.apple.Terminal' },
]

/** Deterministic PRNG, so two runs of the seed produce byte-identical history. */
function mulberry32(a: number): () => number {
  return () => {
    a |= 0
    a = (a + 0x6d2b79f5) | 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

/**
 * Refuses to run anywhere but a local database. The deploy path is the absolute `/data/voice.db`
 * from deploy/docker-compose.yml; the local default is `./data/voice.db` relative to `server/`.
 * The test is "resolves to somewhere under this working directory", which rejects the deploy path
 * and every other absolute path without having to keep a list of them in sync.
 */
function assertLocalDatabase(path: string): void {
  if (path === ':memory:') return
  if (process.env.NODE_ENV === 'production') {
    throw new Error('refusing to seed: NODE_ENV=production')
  }
  const rel = relative(process.cwd(), resolve(path))
  if (rel.startsWith('..') || isAbsolute(rel)) {
    throw new Error(
      `refusing to seed: DATABASE_PATH ${JSON.stringify(path)} is outside ${process.cwd()}. ` +
        'This script only ever writes to a local development database.',
    )
  }
}

function flagInt(rest: string[], name: string, fallback: number): number {
  const i = rest.indexOf(`--${name}`)
  if (i < 0) return fallback
  const n = Number.parseInt(rest[i + 1] ?? '', 10)
  if (!Number.isFinite(n) || n < 1) throw new Error(`--${name} must be a positive integer`)
  return n
}

const [name, ...rest] = process.argv.slice(2)
if (!name || name.startsWith('--')) {
  console.error('usage: seed <user name> [--dictations 200] [--weeks 20] [--streak 12]')
  process.exit(2)
}

const target = flagInt(rest, 'dictations', 200)
const weeks = flagInt(rest, 'weeks', 20)
const streakDays = flagInt(rest, 'streak', 12)

assertLocalDatabase(env.databasePath())
const db = openDb(env.databasePath())

const existing = db.select({ id: users.id }).from(users).where(eq(users.name, name)).get()
const user = existing ?? createUser(db, name)
if (!existing) {
  console.log(`created user ${user.id} (${name})`)
  console.log(`token (shown once): ${issueToken(db, user.id, 'seed')}`)
} else {
  const tokens = db.select({ id: deviceTokens.id }).from(deviceTokens).where(eq(deviceTokens.userId, user.id)).all()
  console.log(`reusing user ${user.id} (${name}), ${tokens.length} existing token(s)`)
}

const rng = mulberry32(0x5eed)
const days = weeks * 7
// A deliberate hole in the middle of the range: a week off. Without it the longest streak is
// trivially the whole window and the streak card proves nothing about the streak code.
const gapStart = Math.floor(days * 0.45)
const gapEnd = gapStart + 9

/** Local midnight `offset` days before today, as a ms epoch. */
function dayStart(offset: number): Date {
  const d = new Date()
  d.setHours(0, 0, 0, 0)
  d.setDate(d.getDate() - offset)
  return d
}

type Planned = { offset: number; at: number }
const planned: Planned[] = []
for (let offset = days - 1; offset >= 0; offset--) {
  const inGap = offset >= gapStart && offset < gapEnd
  const weekend = [0, 6].includes(dayStart(offset).getDay())
  // The most recent `streakDays` always have at least one, so the streak card has something to
  // show and AC-6's "streak outlives the window" case has something to outlive.
  const inStreak = offset < streakDays
  let n: number
  if (inGap && !inStreak) n = 0
  else if (inStreak) n = 1 + Math.floor(rng() * 3)
  else if (weekend) n = rng() < 0.3 ? 1 : 0
  else n = 1 + Math.floor(rng() * 3)
  for (let i = 0; i < n; i++) {
    // Spread across a working day so nothing lands on a midnight boundary, where a one-hour
    // timezone difference would silently move it to the adjacent day.
    const at = dayStart(offset).getTime() + (9 + Math.floor(rng() * 9)) * 3_600_000 + Math.floor(rng() * 3_600_000)
    planned.push({ offset, at })
  }
}
planned.sort((a, b) => a.at - b.at)
const chosen = planned.slice(Math.max(0, planned.length - target))

for (const [i, p] of chosen.entries()) {
  const raw = BENCH_FIXTURES[Math.floor(rng() * BENCH_FIXTURES.length)]!
  const app = APPS[Math.floor(rng() * APPS.length)]!
  // Cleanup drops roughly a fifth of the words; leaving a slice uncleaned exercises the
  // `cleaned ?? raw` branch of the word count and the "no cleanup" case on the dashboard.
  const words = raw.split(/\s+/)
  const cleaned = rng() < 0.8 ? words.filter((_, j) => j % 5 !== 0).join(' ') : null
  const wordCount = (cleaned ?? raw).split(/\s+/).length
  // ~150 words per minute, the rate a person actually dictates at, so the WPM card is plausible.
  const audioMs = Math.round((wordCount / 150) * 60_000 + rng() * 800)
  upsertDictation(db, {
    userId: user.id,
    clientId: `seed-${i}`,
    createdAt: p.at,
    raw,
    cleaned,
    injected: cleaned ? 'cleaned' : 'raw',
    mode: 'clean',
    languageSetting: 'auto',
    languageDetected: /[áàâãéêíóôõúç]/i.test(raw) ? 'pt' : 'en',
    appBundleId: app.bundleId,
    appName: app.name,
    audioMs,
    asrMs: 300 + Math.floor(rng() * 500),
    llmMs: cleaned ? 400 + Math.floor(rng() * 500) : null,
    totalMs: 700 + Math.floor(rng() * 900),
    asrModel: 'large-v3-turbo-632MB',
    llmModel: cleaned ? 'gemma4' : 'skipped',
    clientVersion: '0.2.0',
    fallbackReason: null,
  })
}

const activeDays = new Set(chosen.map((c) => c.offset)).size
console.log(
  `seeded ${chosen.length} dictations for ${name} across ${activeDays} active days in the last ${weeks} weeks ` +
    `(current streak ${streakDays} days, a ${gapEnd - gapStart}-day gap ${gapStart} days back)`,
)
console.log(`database: ${env.databasePath()}`)
db.raw.close()

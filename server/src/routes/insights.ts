import type { FastifyInstance } from 'fastify'
import type { ZodTypeProvider } from 'fastify-type-provider-zod'
import { eq, sql } from 'drizzle-orm'
import { z } from 'zod'
import type { AppDeps } from '../app.js'
import { dictations } from '../db/schema.js'
import { ErrorReply } from './schemas.js'

/**
 * The Insights dashboard's only endpoint (prd-insights-dashboard.md rules 6-20).
 *
 * The single most important property of this file is what it does *not* do: no query below
 * selects `raw` or `cleaned`. That is rule 11, and it is what makes "no transcript text ever
 * leaves the server" structural rather than a matter of review discipline — a handler that never
 * loads a transcript cannot serialize one, however the response shape changes later.
 */

const MS_PER_DAY = 86_400_000

/**
 * One formatter per zone, built on demand and kept.
 *
 * Constructing an `Intl.DateTimeFormat` is expensive and the day bucketing calls this once per
 * dictation, so building one per row would dominate the request. The cache cannot grow without
 * bound even though `tz` is caller-supplied: an unknown zone throws before it is ever stored, and
 * the set of zones ICU will accept is fixed at a few hundred.
 */
const formatters = new Map<string, Intl.DateTimeFormat>()

function formatterFor(tz: string): Intl.DateTimeFormat {
  const cached = formatters.get(tz)
  if (cached) return cached
  // Throws RangeError on an unknown zone. `en-CA` is the shortest route to ISO ordering.
  const f = new Intl.DateTimeFormat('en-CA', { timeZone: tz, year: 'numeric', month: '2-digit', day: '2-digit' })
  formatters.set(tz, f)
  return f
}

/**
 * Rule 7, with one deviation from its letter that the PRD's own reasoning demands.
 *
 * The PRD says to validate against `Intl.supportedValuesOf('timeZone')`. That list is 418 entries
 * on this Node build and it deliberately contains only canonical zones — measured here, it
 * excludes `UTC`, every `Etc/*` zone, and every alias (`Asia/Calcutta`, `America/Buenos_Aires`).
 * Validating against it would return 400 for zones the very next line formats perfectly well, so
 * validation and use would disagree about what a valid zone is.
 *
 * Asking the formatter instead makes them the same question: if `Intl` can bucket days in this
 * zone, it is accepted; if it cannot, it is a 400. That is what rule 7 is actually for — a typo
 * must never fall back silently to UTC and hand the user a plausible wrong streak.
 */
export function isValidTimeZone(tz: string): boolean {
  try {
    formatterFor(tz)
    return true
  } catch {
    return false
  }
}

const Query = z.object({
  // Absent means UTC, which is a real zone and an honest answer for a caller that did not say.
  // An *invalid* tz is a 400 (rule 7) — silently falling back would produce a plausible wrong
  // streak, which is the kind of wrong nobody ever notices.
  tz: z.string().min(1).max(64).default('UTC'),
  // Bounded so a caller cannot ask for an unbounded heatmap. The streak ignores this entirely.
  weeks: z.coerce.number().int().min(1).max(53).default(21),
})

const InsightsReply = z.object({
  totals: z.object({
    dictations: z.number().int(),
    words: z.number().int(),
    audioMs: z.number().int(),
    wpm: z.number().int().nullable(),
  }),
  streak: z.object({ current: z.number().int(), longest: z.number().int() }),
  days: z.array(z.object({ date: z.string(), dictations: z.number().int(), words: z.number().int() })),
  apps: z.array(z.object({ label: z.string(), dictations: z.number().int(), share: z.number().int() })),
  generatedAt: z.number().int(),
})
export type InsightsReply = z.infer<typeof InsightsReply>

/** Below this much recorded audio, a words-per-minute figure is arithmetic, not information. */
export const WPM_MIN_AUDIO_MS = 60_000

/** Top apps shown by name; everything past this is folded into `Other` (rule 19). */
export const APP_ROWS = 6

/**
 * `YYYY-MM-DD` for a ms epoch in `tz`. Intl is the point: SQLite has no timezone database, so a
 * day boundary computed there would be wrong for every historical day on the far side of a DST
 * change (rule 8).
 */
export function localDayKey(ts: number, tz: string): string {
  return formatterFor(tz).format(new Date(ts))
}

/**
 * `YYYY-MM-DD` → days since the epoch, by reading the key as a UTC date.
 *
 * The key already *is* a local calendar date; re-anchoring it at UTC midnight is what makes
 * "is the next day adjacent" plain integer arithmetic, because UTC has no DST and every day in it
 * is exactly 24 hours. Doing the same arithmetic in the original zone would make the day after a
 * spring-forward 23 hours long and silently break a streak.
 */
export function dayNumber(key: string): number {
  const [y, m, d] = key.split('-').map(Number) as [number, number, number]
  return Date.UTC(y, m - 1, d) / MS_PER_DAY
}

export function dayKey(n: number): string {
  return new Date(n * MS_PER_DAY).toISOString().slice(0, 10)
}

/**
 * Consecutive local calendar days with at least one dictation (rule 17).
 *
 * Counting starts at yesterday when today is empty. Without that, the streak reads 0 every
 * morning until the first dictation of the day — wrong, and discouraging at exactly the moment
 * the number is supposed to encourage.
 *
 * `longest` scans all of history, and so does `current`: rule 13 keeps both out of the `weeks`
 * window, because a 60-day streak viewed through a 21-week window must still read 60.
 */
export function computeStreak(activeDays: Set<number>, today: number): { current: number; longest: number } {
  let current = 0
  let cursor = activeDays.has(today) ? today : activeDays.has(today - 1) ? today - 1 : null
  while (cursor !== null && activeDays.has(cursor)) {
    current++
    cursor--
  }

  let longest = 0
  let run = 0
  const sorted = [...activeDays].sort((a, b) => a - b)
  for (const [i, d] of sorted.entries()) {
    run = i > 0 && d === sorted[i - 1]! + 1 ? run + 1 : 1
    if (run > longest) longest = run
  }
  return { current, longest }
}

/**
 * Rounds shares to integers that sum to exactly 100 (rule 19), by the largest-remainder method:
 * floor everything, then hand the leftover points to whoever lost the most in the rounding.
 * Rounding each share independently would sum to 98 or 101 and make the card look broken.
 */
export function allocateShares(counts: number[]): number[] {
  const total = counts.reduce((a, b) => a + b, 0)
  if (total === 0) return counts.map(() => 0)
  const exact = counts.map((c) => (c * 100) / total)
  const shares = exact.map(Math.floor)
  const leftover = 100 - shares.reduce((a, b) => a + b, 0)
  const byRemainder = exact
    .map((e, i) => ({ i, frac: e - Math.floor(e) }))
    .sort((a, b) => b.frac - a.frac || a.i - b.i)
  for (let k = 0; k < leftover; k++) {
    const idx = byRemainder[k % byRemainder.length]!.i
    shares[idx] = shares[idx]! + 1
  }
  return shares
}

/**
 * The heatmap window: `weeks` complete Sun–Sat weeks plus the current, partial week (rule 18).
 * Returned as a day-number range so the caller can zero-fill it — every day in the window appears
 * in the response, including the empty ones (rule 20), because a client that has to infer gaps
 * from missing keys is a client that will eventually draw the wrong week.
 */
export function heatmapWindow(today: number, weeks: number): { from: number; to: number } {
  const dayOfWeek = new Date(today * MS_PER_DAY).getUTCDay() // 0 = Sunday
  return { from: today - dayOfWeek - weeks * 7, to: today }
}

export function registerInsights(app: FastifyInstance, deps: AppDeps): void {
  app.withTypeProvider<ZodTypeProvider>().get('/v1/insights', {
    schema: { querystring: Query, response: { 200: InsightsReply, 400: ErrorReply } },
  }, async (req, reply) => {
    const { tz, weeks } = req.query
    if (!isValidTimeZone(tz)) {
      return reply.code(400).send({ error: 'bad_request', message: `Unknown IANA time zone ${JSON.stringify(tz)}` })
    }
    const userId = req.user.id

    // Every query below filters on the caller's own user id (rule 13 in the build spec). There is
    // no cross-user aggregate anywhere in this handler and no parameter that could ask for one.
    const totals = deps.db
      .select({
        dictations: sql<number>`count(*)`,
        // SUM skips NULLs, which is exactly rule 12: an un-backfilled row counts as a dictation
        // but contributes no words. It must never be coerced to 0 per-row — that would invent a
        // number that looks right.
        words: sql<number>`coalesce(sum(${dictations.wordCount}), 0)`,
        audioMs: sql<number>`coalesce(sum(case when ${dictations.audioMs} > 0 then ${dictations.audioMs} end), 0)`,
        // The WPM numerator is restricted to the same rows as its denominator; mixing a total over
        // all rows with audio from only some would overstate the rate (rule 15).
        wordsWithAudio: sql<number>`coalesce(sum(case when ${dictations.audioMs} > 0 then ${dictations.wordCount} end), 0)`,
      })
      .from(dictations)
      .where(eq(dictations.userId, userId))
      .get()!

    const rows = deps.db
      .select({ createdAt: dictations.createdAt, wordCount: dictations.wordCount })
      .from(dictations)
      .where(eq(dictations.userId, userId))
      .all()

    const perDay = new Map<number, { dictations: number; words: number }>()
    for (const r of rows) {
      const n = dayNumber(localDayKey(r.createdAt, tz))
      const bucket = perDay.get(n) ?? { dictations: 0, words: 0 }
      bucket.dictations++
      bucket.words += r.wordCount ?? 0
      perDay.set(n, bucket)
    }

    const generatedAt = Date.now()
    const today = dayNumber(localDayKey(generatedAt, tz))
    const streak = computeStreak(new Set(perDay.keys()), today)

    const { from, to } = heatmapWindow(today, weeks)
    const days: InsightsReply['days'] = []
    for (let n = from; n <= to; n++) {
      const bucket = perDay.get(n)
      days.push({ date: dayKey(n), dictations: bucket?.dictations ?? 0, words: bucket?.words ?? 0 })
    }

    const appRows = deps.db
      .select({ appName: dictations.appName, appBundleId: dictations.appBundleId, n: sql<number>`count(*)` })
      .from(dictations)
      .where(eq(dictations.userId, userId))
      .groupBy(dictations.appName, dictations.appBundleId)
      .all()

    // Two rows can collapse to one label — the same app seen under a changed bundle id, or two
    // unlabelled apps both falling through to `Unknown` — so merge by label before ranking.
    const byLabel = new Map<string, number>()
    for (const a of appRows) {
      const label = a.appName ?? a.appBundleId ?? 'Unknown'
      byLabel.set(label, (byLabel.get(label) ?? 0) + a.n)
    }
    const ranked = [...byLabel.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
    const head = ranked.slice(0, APP_ROWS)
    const tail = ranked.slice(APP_ROWS)
    const tailCount = tail.reduce((sum, [, n]) => sum + n, 0)
    // `Other` only appears when something is actually in it; a row reading "Other 0%" is noise.
    const entries = tailCount > 0 ? [...head, ['Other', tailCount] as [string, number]] : head
    const shares = allocateShares(entries.map(([, n]) => n))
    const apps = entries.map(([label, n], i) => ({ label, dictations: n, share: shares[i]! }))

    // Rule 16: below a minute of recorded audio this is arithmetic, not information.
    const wpm =
      totals.audioMs >= WPM_MIN_AUDIO_MS
        ? Math.round(totals.wordsWithAudio / (totals.audioMs / 60_000))
        : null

    req.log.info({ userId, dictations: totals.dictations, weeks, tz }, 'insights')

    // The zod response schema is the last line of defence for rule 6: fastify serializes through
    // it, so a key that is not declared above cannot reach the client even if a future edit
    // accidentally puts one in the object.
    return {
      totals: { dictations: totals.dictations, words: totals.words, audioMs: totals.audioMs, wpm },
      streak,
      days,
      apps,
      generatedAt,
    }
  })
}

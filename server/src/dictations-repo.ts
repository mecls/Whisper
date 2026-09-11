import { and, desc, eq, isNull, lt, sql } from 'drizzle-orm'
import { nanoid } from 'nanoid'
import type { Db } from './db/client.js'
import { dictations, type FallbackReason, type Injected } from './db/schema.js'

export interface DictationUpsert {
  userId: string
  clientId: string
  createdAt: number
  raw: string
  cleaned: string | null
  injected: Injected | null
  mode: string
  languageSetting: string
  languageDetected: string | null
  appBundleId: string | null
  appName: string | null
  audioMs: number | null
  asrMs: number | null
  llmMs: number | null
  totalMs: number | null
  asrModel: string | null
  llmModel: string | null
  clientVersion: string | null
  fallbackReason: FallbackReason | null
}

/**
 * The one definition of a word in this codebase (prd-insights-dashboard.md rule 5): whitespace-
 * separated tokens after trimming. Deliberately crude — it counts "well-formed" as one word and
 * "€25" as one word, which is what a person eyeballing a dashboard expects.
 *
 * It has exactly two callers, `upsertDictation` and `backfillWordCounts`, and it must stay that
 * way. The moment a second definition exists, a backfilled row and a freshly written row start
 * disagreeing about the same sentence and the headline number drifts with no way to tell when it
 * started. The insights endpoint never calls this: it reads the stored column, because calling it
 * would mean SELECTing a transcript.
 */
export function countWords(text: string): number {
  const trimmed = text.trim()
  return trimmed === '' ? 0 : trimmed.split(/\s+/).length
}

/**
 * Insert or update on (user_id, client_id). A replay never erases a cleanup that already
 * happened (`cleaned` is only set when non-null) and keeps the first fallback reason.
 */
export function upsertDictation(db: Db, row: DictationUpsert): { id: string } {
  const id = nanoid()
  // The count is computed here, from the payload, because this is the only place that has the
  // text in hand without having to read it back out of the table (rule 6: one statement, no
  // read-back). `cleaned ?? raw` is the text that actually got pasted.
  const wordCount = countWords(row.cleaned ?? row.raw)
  db.insert(dictations)
    .values({ id, ...row, wordCount })
    .onConflictDoUpdate({
      target: [dictations.userId, dictations.clientId],
      set: {
        cleaned: sql`coalesce(excluded.cleaned, ${dictations.cleaned})`,
        injected: sql`coalesce(excluded.injected, ${dictations.injected})`,
        fallbackReason: sql`coalesce(${dictations.fallbackReason}, excluded.fallback_reason)`,
        llmMs: sql`coalesce(excluded.llm_ms, ${dictations.llmMs})`,
        totalMs: sql`coalesce(excluded.total_ms, ${dictations.totalMs})`,
        llmModel: sql`coalesce(excluded.llm_model, ${dictations.llmModel})`,
        languageDetected: sql`coalesce(excluded.language_detected, ${dictations.languageDetected})`,
        // Exact, and it has to be, because nothing reconciles it afterwards. `raw` is frozen at
        // first insert and `cleaned` resolves to coalesce(excluded.cleaned, dictations.cleaned)
        // just above, so the only way this row's pasted text can change is a new non-null
        // `cleaned` — which is precisely the branch the caller had the text for.
        //
        // The ELSE is load-bearing. Without it, an outbox replay carrying `cleaned: null` for a
        // row the server had already cleaned would overwrite a cleaned count with the raw count,
        // silently inflating the user's word total by every filler the cleanup had removed.
        wordCount: sql`case when excluded.cleaned is not null then excluded.word_count else ${dictations.wordCount} end`,
      },
    })
    .run()
  const got = db.select({ id: dictations.id }).from(dictations)
    .where(and(eq(dictations.userId, row.userId), eq(dictations.clientId, row.clientId))).get()!
  return { id: got.id }
}

/**
 * Sets the injection outcome, and `total_ms` with it when the client measured one.
 *
 * `total_ms` arrives here rather than on the refine call because release→paste is only known
 * once the paste has happened, which is strictly after `/v1/refine` has already written the row.
 * An absent `totalMs` leaves whatever is stored alone: a client that cannot measure must never
 * blank out a measurement a previous call recorded.
 */
export function setInjected(db: Db, userId: string, clientId: string, injected: Injected, totalMs?: number): boolean {
  const r = db.update(dictations).set(totalMs === undefined ? { injected } : { injected, totalMs })
    .where(and(eq(dictations.userId, userId), eq(dictations.clientId, clientId))).run()
  return r.changes > 0
}

export function listDictations(db: Db, userId: string, limit: number, before?: number) {
  const where = before ? and(eq(dictations.userId, userId), lt(dictations.createdAt, before)) : eq(dictations.userId, userId)
  const items = db.select().from(dictations).where(where).orderBy(desc(dictations.createdAt)).limit(limit).all()
  const nextBefore = items.length === limit ? items[items.length - 1]!.createdAt : null
  return { items, nextBefore }
}

/**
 * Fills in `word_count` for rows written before the column existed (rule 7).
 *
 * This is the only place outside the write path that reads `raw`/`cleaned`, and it runs once at
 * boot, never in a request. It is deliberately chunked: the alternative, a single
 * `update ... set word_count = <sql word counter>`, would mean writing a second definition of a
 * word in SQL that would drift from `countWords` the first time either changed.
 *
 * Idempotent — it only ever touches rows where the column is still NULL, so running it twice is a
 * no-op. Callers must treat a throw as non-fatal: a partially backfilled table understates the
 * word total, which is visibly wrong and self-corrects on the next boot, whereas refusing to
 * start means no dictation gets saved at all.
 */
export function backfillWordCounts(db: Db, chunkSize = 500): number {
  let filled = 0
  for (;;) {
    const batch = db
      .select({ id: dictations.id, raw: dictations.raw, cleaned: dictations.cleaned })
      .from(dictations)
      .where(isNull(dictations.wordCount))
      .limit(chunkSize)
      .all()
    if (batch.length === 0) return filled
    db.raw.transaction(() => {
      for (const r of batch) {
        db.update(dictations)
          .set({ wordCount: countWords(r.cleaned ?? r.raw) })
          .where(eq(dictations.id, r.id))
          .run()
      }
    })()
    filled += batch.length
    // A short final batch means the table is exhausted; one more query would only confirm it.
    if (batch.length < chunkSize) return filled
  }
}

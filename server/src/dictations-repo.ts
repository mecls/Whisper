import { and, desc, eq, lt, sql } from 'drizzle-orm'
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
 * Insert or update on (user_id, client_id). A replay never erases a cleanup that already
 * happened (`cleaned` is only set when non-null) and keeps the first fallback reason.
 */
export function upsertDictation(db: Db, row: DictationUpsert): { id: string } {
  const id = nanoid()
  db.insert(dictations)
    .values({ id, ...row })
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

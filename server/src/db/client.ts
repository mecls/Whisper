import Database from 'better-sqlite3'
import { drizzle, type BetterSQLite3Database } from 'drizzle-orm/better-sqlite3'
import { mkdirSync } from 'node:fs'
import { dirname } from 'node:path'
import * as schema from './schema.js'
import { applyMigrations } from './migrate.js'

export type Db = BetterSQLite3Database<typeof schema> & { raw: Database.Database }

/** Opens (creating if needed) the SQLite file, sets WAL, applies pending migrations. */
export function openDb(path: string): Db {
  if (path !== ':memory:') mkdirSync(dirname(path), { recursive: true })
  const raw = new Database(path)
  raw.pragma('journal_mode = WAL')
  raw.pragma('foreign_keys = ON')
  raw.pragma('busy_timeout = 5000')
  applyMigrations(raw)
  const db = drizzle(raw, { schema }) as unknown as Db
  db.raw = raw
  return db
}

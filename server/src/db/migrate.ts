import type Database from 'better-sqlite3'
import { readdirSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

const MIGRATIONS_DIR = join(dirname(fileURLToPath(import.meta.url)), 'migrations')

/** Hand-written SQL files applied once each, in name order, tracked in `_migrations`. */
export function applyMigrations(raw: Database.Database): string[] {
  raw.exec('create table if not exists _migrations (name text primary key, applied_at integer not null)')
  const applied = new Set((raw.prepare('select name from _migrations').all() as { name: string }[]).map((r) => r.name))
  const files = readdirSync(MIGRATIONS_DIR).filter((f) => f.endsWith('.sql')).sort()
  const ran: string[] = []
  for (const f of files) {
    if (applied.has(f)) continue
    const sql = readFileSync(join(MIGRATIONS_DIR, f), 'utf8')
    raw.transaction(() => {
      raw.exec(sql)
      raw.prepare('insert into _migrations (name, applied_at) values (?, ?)').run(f, Date.now())
    })()
    ran.push(f)
  }
  return ran
}

// `npm run migrate` — apply to the configured database and exit.
if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const { openDb } = await import('./client.js')
  const { env } = await import('../env.js')
  const db = openDb(env.databasePath())
  console.log(`database ${env.databasePath()} up to date`)
  db.raw.close()
}

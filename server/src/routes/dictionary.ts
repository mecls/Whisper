import { isNull, or, eq } from 'drizzle-orm'
import type { Db } from '../db/client.js'
import { dictionaryEntries } from '../db/schema.js'

export function loadDictionary(db: Db, userId: string) {
  return db.select({ term: dictionaryEntries.term, replacement: dictionaryEntries.replacement }).from(dictionaryEntries)
    .where(or(isNull(dictionaryEntries.userId), eq(dictionaryEntries.userId, userId))).all()
}

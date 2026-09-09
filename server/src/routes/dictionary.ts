import type { FastifyInstance } from 'fastify'
import type { ZodTypeProvider } from 'fastify-type-provider-zod'
import { and, eq, isNull, or } from 'drizzle-orm'
import { nanoid } from 'nanoid'
import { z } from 'zod'
import type { AppDeps } from '../app.js'
import type { Db } from '../db/client.js'
import { dictionaryEntries, now } from '../db/schema.js'
import { ErrorReply } from './schemas.js'

export function loadDictionary(db: Db, userId: string) {
  return db.select({ term: dictionaryEntries.term, replacement: dictionaryEntries.replacement }).from(dictionaryEntries)
    .where(or(isNull(dictionaryEntries.userId), eq(dictionaryEntries.userId, userId))).all()
}

const Entry = z.object({ id: z.string(), term: z.string(), replacement: z.string().nullable(), note: z.string().nullable(), teamWide: z.boolean(), createdAt: z.number() })
const NewEntry = z.object({ term: z.string().min(1).max(80), replacement: z.string().min(1).max(80).optional(), note: z.string().max(200).optional(), teamWide: z.boolean().default(false) })

export function registerDictionary(app: FastifyInstance, deps: AppDeps): void {
  const r = app.withTypeProvider<ZodTypeProvider>()
  const toEntry = (row: typeof dictionaryEntries.$inferSelect) => ({ id: row.id, term: row.term, replacement: row.replacement, note: row.note, teamWide: row.userId === null, createdAt: row.createdAt })

  r.get('/v1/dictionary', { schema: { response: { 200: z.array(Entry) } } }, async (req) =>
    deps.db.select().from(dictionaryEntries).where(or(isNull(dictionaryEntries.userId), eq(dictionaryEntries.userId, req.user.id))).all().map(toEntry))

  r.post('/v1/dictionary', { schema: { body: NewEntry, response: { 200: Entry } } }, async (req) => {
    const row = { id: nanoid(), userId: req.body.teamWide ? null : req.user.id, term: req.body.term, replacement: req.body.replacement ?? null, note: req.body.note ?? null, createdAt: now() }
    deps.db.insert(dictionaryEntries).values(row).run()
    return toEntry(row)
  })

  r.delete('/v1/dictionary/:id', { schema: { params: z.object({ id: z.string() }), response: { 200: z.object({ ok: z.literal(true) }), 404: ErrorReply } } }, async (req, reply) => {
    const res = deps.db.delete(dictionaryEntries)
      .where(and(eq(dictionaryEntries.id, req.params.id), or(isNull(dictionaryEntries.userId), eq(dictionaryEntries.userId, req.user.id)))).run()
    if (res.changes === 0) return reply.code(404).send({ error: 'not_found', message: 'No such entry' })
    return { ok: true as const }
  })
}

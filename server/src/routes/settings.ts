import type { FastifyInstance } from 'fastify'
import type { ZodTypeProvider } from 'fastify-type-provider-zod'
import { eq } from 'drizzle-orm'
import { z } from 'zod'
import type { AppDeps } from '../app.js'
import type { Db } from '../db/client.js'
import { userSettings, now } from '../db/schema.js'

export const SettingsSchema = z.object({
  mode: z.enum(['clean', 'literal']),
  language: z.enum(['auto', 'pt', 'en']),
  hotkey: z.enum(['fn', 'rightOption', 'rightCommand']),
  llmModel: z.string().min(1).max(80).optional(),
})
export type Settings = z.infer<typeof SettingsSchema>
export const DEFAULT_SETTINGS: Settings = { mode: 'clean', language: 'auto', hotkey: 'fn' }

export function getSettings(db: Db, userId: string): Settings {
  const row = db.select().from(userSettings).where(eq(userSettings.userId, userId)).get()
  if (!row) return DEFAULT_SETTINGS
  const parsed = SettingsSchema.safeParse(JSON.parse(row.json))
  return parsed.success ? parsed.data : DEFAULT_SETTINGS
}

export function putSettings(db: Db, userId: string, s: Settings): void {
  db.insert(userSettings).values({ userId, json: JSON.stringify(s), updatedAt: now() })
    .onConflictDoUpdate({ target: userSettings.userId, set: { json: JSON.stringify(s), updatedAt: now() } }).run()
}

export function registerSettings(app: FastifyInstance, deps: AppDeps): void {
  const r = app.withTypeProvider<ZodTypeProvider>()
  r.get('/v1/settings', { schema: { response: { 200: SettingsSchema } } }, async (req) => getSettings(deps.db, req.user.id))
  r.put('/v1/settings', { schema: { body: SettingsSchema, response: { 200: SettingsSchema } } }, async (req) => {
    putSettings(deps.db, req.user.id, req.body)
    return req.body
  })
}

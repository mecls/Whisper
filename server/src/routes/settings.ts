import type { FastifyInstance, FastifyReply } from 'fastify'
import type { ZodTypeProvider } from 'fastify-type-provider-zod'
import { eq } from 'drizzle-orm'
import { z } from 'zod'
import type { AppDeps } from '../app.js'
import type { Db } from '../db/client.js'
import { userSettings, now } from '../db/schema.js'
import { env } from '../env.js'

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
  r.put('/v1/settings', {
    // No `400` entry in `response`: declaring one would make fastify-type-provider-zod
    // serialize EVERY 400 on this route (including zod's own body-validation failures)
    // through that schema, silently stripping their `statusCode`/`code` fields and
    // diverging from the documented shape (docs/API.md) for this one route only.
    schema: { body: SettingsSchema, response: { 200: SettingsSchema } },
  }, async (req, reply) => {
    if (req.body.llmModel !== undefined && !env.llmAllowedModels().includes(req.body.llmModel)) {
      // Escape the zod type-provider's reply typing (narrowed to the declared `200`
      // schema only — see the comment above) just for this one, deliberately
      // undeclared, 400.
      return (reply as FastifyReply)
        .code(400)
        .send({ error: 'unknown_model', message: `llmModel must be one of: ${env.llmAllowedModels().join(', ')}` })
    }
    putSettings(deps.db, req.user.id, req.body)
    return req.body
  })
}

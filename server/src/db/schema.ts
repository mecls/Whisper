import { sqliteTable, text, integer, index, uniqueIndex } from 'drizzle-orm/sqlite-core'

// Timestamps are ms epoch integers (house convention). Ids are nanoid.

export const users = sqliteTable('users', {
  id: text('id').primaryKey(),
  name: text('name').notNull(),
  createdAt: integer('created_at').notNull(),
  disabledAt: integer('disabled_at'),
})

export const deviceTokens = sqliteTable(
  'device_tokens',
  {
    id: text('id').primaryKey(),
    userId: text('user_id').notNull().references(() => users.id),
    tokenHash: text('token_hash').notNull(),
    label: text('label').notNull(),
    createdAt: integer('created_at').notNull(),
    lastUsedAt: integer('last_used_at'),
    revokedAt: integer('revoked_at'),
  },
  (t) => [uniqueIndex('device_tokens_hash').on(t.tokenHash)],
)

export const FALLBACK_REASONS = [
  'client-timeout', 'offline', 'unauthorized', 'server',
  'llm-timeout', 'llm-error', 'llm-busy', 'llm-truncated', 'guard-rejected',
] as const
export type FallbackReason = (typeof FALLBACK_REASONS)[number]

export const INJECTED = ['cleaned', 'raw', 'none', 'clipboard'] as const
export type Injected = (typeof INJECTED)[number]

export const dictations = sqliteTable(
  'dictations',
  {
    id: text('id').primaryKey(),
    userId: text('user_id').notNull().references(() => users.id),
    clientId: text('client_id').notNull(),
    createdAt: integer('created_at').notNull(),
    raw: text('raw').notNull(),
    cleaned: text('cleaned'),
    injected: text('injected', { enum: INJECTED }),
    mode: text('mode').notNull(),
    languageSetting: text('language_setting').notNull(),
    languageDetected: text('language_detected'),
    appBundleId: text('app_bundle_id'),
    appName: text('app_name'),
    audioMs: integer('audio_ms'),
    asrMs: integer('asr_ms'),
    llmMs: integer('llm_ms'),
    // Release→paste, measured on the client (prd-sub-second-dictation.md rule 15). Distinct from
    // asrMs + llmMs: it includes injection and every hop of dispatch between the stages, which is
    // what the user actually feels and where the unexplained latency has historically hidden.
    totalMs: integer('total_ms'),
    asrModel: text('asr_model'),
    llmModel: text('llm_model'),
    clientVersion: text('client_version'),
    fallbackReason: text('fallback_reason', { enum: FALLBACK_REASONS }),
  },
  (t) => [
    uniqueIndex('dictations_user_client').on(t.userId, t.clientId),
    index('dictations_user_created').on(t.userId, t.createdAt),
  ],
)

export const userSettings = sqliteTable('user_settings', {
  userId: text('user_id').primaryKey().references(() => users.id),
  json: text('json').notNull(),
  updatedAt: integer('updated_at').notNull(),
})

export const dictionaryEntries = sqliteTable(
  'dictionary_entries',
  {
    id: text('id').primaryKey(),
    userId: text('user_id').references(() => users.id), // null = team-wide
    term: text('term').notNull(),
    replacement: text('replacement'),
    note: text('note'),
    createdAt: integer('created_at').notNull(),
  },
  (t) => [index('dictionary_user').on(t.userId)],
)

export const now = (): number => Date.now()

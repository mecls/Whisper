import { createHash, randomBytes } from 'node:crypto'
import { eq } from 'drizzle-orm'
import { nanoid } from 'nanoid'
import type { Db } from './db/client.js'
import { deviceTokens, users, now } from './db/schema.js'

export type AuthUser = { id: string; name: string }
export type AuthResult =
  | { ok: true; user: AuthUser; tokenId: string }
  | { ok: false; error: 'missing_token' | 'invalid_token' | 'token_revoked' | 'user_disabled' }

const LAST_USED_THROTTLE_MS = 5 * 60_000

export function hashToken(token: string): string {
  return createHash('sha256').update(token).digest('hex')
}

/** `mv_` + 32 random bytes base64url. Shown once; only the hash is stored. */
export function generateToken(): { token: string; hash: string } {
  const token = `mv_${randomBytes(32).toString('base64url')}`
  return { token, hash: hashToken(token) }
}

export function createUser(db: Db, name: string): AuthUser {
  const id = nanoid()
  db.insert(users).values({ id, name, createdAt: now() }).run()
  return { id, name }
}

export function issueToken(db: Db, userId: string, label: string): string {
  const { token, hash } = generateToken()
  db.insert(deviceTokens).values({ id: nanoid(), userId, tokenHash: hash, label, createdAt: now() }).run()
  return token
}

export function revokeToken(db: Db, tokenId: string): void {
  db.update(deviceTokens).set({ revokedAt: now() }).where(eq(deviceTokens.id, tokenId)).run()
}

/** Extracts the raw `mv_...` token from an `Authorization: Bearer mv_...` header, or null. */
export function parseBearer(header: string | undefined): string | null {
  const m = header?.match(/^Bearer\s+(mv_[A-Za-z0-9_-]+)$/)
  return m ? m[1]! : null
}

export function authenticate(db: Db, header: string | undefined, nowMs: number = now()): AuthResult {
  const token = parseBearer(header)
  if (!token) return { ok: false, error: 'missing_token' }
  const hash = hashToken(token)
  const row = db
    .select({
      tokenId: deviceTokens.id, revokedAt: deviceTokens.revokedAt, lastUsedAt: deviceTokens.lastUsedAt,
      userId: users.id, name: users.name, disabledAt: users.disabledAt,
    })
    .from(deviceTokens)
    .innerJoin(users, eq(users.id, deviceTokens.userId))
    .where(eq(deviceTokens.tokenHash, hash))
    .get()
  if (!row) return { ok: false, error: 'invalid_token' }
  if (row.revokedAt) return { ok: false, error: 'token_revoked' }
  if (row.disabledAt) return { ok: false, error: 'user_disabled' }
  if (!row.lastUsedAt || nowMs - row.lastUsedAt >= LAST_USED_THROTTLE_MS) {
    db.update(deviceTokens).set({ lastUsedAt: nowMs }).where(eq(deviceTokens.id, row.tokenId)).run()
  }
  return { ok: true, user: { id: row.userId, name: row.name }, tokenId: row.tokenId }
}

/**
 * Per-IP failed-authentication counter (spec §4.2: "a per-IP limit on 401s so a
 * scanner cannot fill the logs"). `@fastify/rate-limit`'s keyGenerator alone does
 * not deliver this because every random well-formed token gets its own bucket.
 *
 * Kept deliberately small and dependency-free so it is unit-testable without HTTP.
 */
export class FailedAuthLimiter {
  private readonly entries = new Map<string, { count: number; windowStart: number }>()

  constructor(
    private readonly maxFailures = 30,
    private readonly windowMs = 60_000,
  ) {}

  /** True when `ip` has already hit the failure ceiling within the current window. */
  shouldBlock(ip: string, nowMs: number): boolean {
    const entry = this.entries.get(ip)
    if (!entry) return false
    if (nowMs - entry.windowStart >= this.windowMs) return false
    return entry.count >= this.maxFailures
  }

  /** Record one failed authentication for `ip`, starting a fresh window if the old one expired. */
  record(ip: string, nowMs: number): void {
    const entry = this.entries.get(ip)
    if (!entry || nowMs - entry.windowStart >= this.windowMs) {
      this.entries.set(ip, { count: 1, windowStart: nowMs })
    } else {
      entry.count += 1
    }
    // Unbounded growth guard: a distributed scanner hitting many IPs would otherwise
    // leak memory forever. Only sweep once the map gets large — this keeps the common
    // case (few distinct IPs) allocation-free.
    if (this.entries.size > 10_000) {
      for (const [key, e] of this.entries) {
        if (nowMs - e.windowStart >= this.windowMs) this.entries.delete(key)
      }
    }
  }

  /** Test/introspection only. */
  get size(): number {
    return this.entries.size
  }
}

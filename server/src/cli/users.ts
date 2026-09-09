import { eq } from 'drizzle-orm'
import { openDb } from '../db/client.js'
import { env } from '../env.js'
import { createUser, issueToken, revokeToken } from '../auth.js'
import { deviceTokens, users } from '../db/schema.js'

const [cmd, ...rest] = process.argv.slice(2)
const db = openDb(env.databasePath())

function usage(): never {
  console.error('usage: users add <name> [--label <label>] | users list | users token <userId> [--label <label>] | users revoke <tokenId>')
  process.exit(2)
}

function flag(name: string, fallback: string): string {
  const i = rest.indexOf(`--${name}`)
  return i >= 0 && rest[i + 1] ? rest[i + 1]! : fallback
}

switch (cmd) {
  case 'add': {
    const name = rest[0]
    if (!name || name.startsWith('--')) usage()
    const u = createUser(db, name)
    const token = issueToken(db, u.id, flag('label', 'default'))
    console.log(`user ${u.id} (${u.name})`)
    console.log(`token (shown once): ${token}`)
    break
  }
  case 'token': {
    const userId = rest[0]
    if (!userId) usage()
    const token = issueToken(db, userId, flag('label', 'default'))
    console.log(`token (shown once): ${token}`)
    break
  }
  case 'list': {
    const rows = db
      .select({ userId: users.id, name: users.name, disabledAt: users.disabledAt, tokenId: deviceTokens.id, label: deviceTokens.label, lastUsedAt: deviceTokens.lastUsedAt, revokedAt: deviceTokens.revokedAt })
      .from(users)
      .leftJoin(deviceTokens, eq(deviceTokens.userId, users.id))
      .all()
    for (const r of rows) {
      const state = r.revokedAt ? 'revoked' : r.disabledAt ? 'disabled' : 'active'
      console.log(`${r.userId}\t${r.name}\t${r.tokenId ?? '-'}\t${r.label ?? '-'}\t${state}\tlast_used=${r.lastUsedAt ? new Date(r.lastUsedAt).toISOString() : 'never'}`)
    }
    break
  }
  case 'revoke': {
    const tokenId = rest[0]
    if (!tokenId) usage()
    revokeToken(db, tokenId)
    console.log(`revoked ${tokenId}`)
    break
  }
  default:
    usage()
}
db.raw.close()

import { readFileSync } from 'node:fs'
import { buildApp } from './app.js'
import { env } from './env.js'
import { openDb } from './db/client.js'
import { makeOllamaCaller, probeModels } from './llm/client.js'
import { Semaphore } from './llm/semaphore.js'

const version = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8')).version as string
// Fail fast on boot, not on the first request: an operator who sets LOG_TRANSCRIPTS=1
// in a prod .env by mistake should see this in `docker compose up`, not discover it
// hours later when /v1/refine starts 500ing.
env.logTranscripts()
const db = openDb(env.databasePath())
const app = buildApp({
  db,
  llm: makeOllamaCaller(),
  version,
  llmSemaphore: new Semaphore(env.llmConcurrency()),
  probeLlm: probeModels,
})

const shutdown = async (signal: string) => {
  app.log.info({ signal }, 'shutting down')
  await app.close()
  process.exit(0)
}
process.on('SIGTERM', () => void shutdown('SIGTERM'))
process.on('SIGINT', () => void shutdown('SIGINT'))

app.listen({ port: env.port(), host: '0.0.0.0' }).catch((err) => {
  app.log.error(err)
  process.exit(1)
})

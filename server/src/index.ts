import { readFileSync } from 'node:fs'
import { buildApp } from './app.js'
import { env } from './env.js'

const version = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8')).version as string
const app = buildApp({ db: null, llm: null, version })

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

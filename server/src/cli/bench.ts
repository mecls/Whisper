/**
 * Bench CLI: models × reasoning × concurrency against Ollama Cloud.
 *
 * `npm run bench` (from `server/`) with `LLM_API_KEY` set. Prints a markdown
 * results table on stdout (paste into docs/PROMPT.md) and per-fixture detail
 * on stderr (redirect with `2>bench-detail.log` to inspect quality without
 * polluting the table).
 */
import { makeOllamaCaller } from '../llm/client.js'
import { refineText } from '../llm/refine.js'
import { BENCH_FIXTURES } from './bench-fixtures.js'

const MATRIX: [string, string][] = [['gemma4', 'none'], ['gpt-oss:120b', 'low'], ['qwen3.5', 'none']]
const llm = makeOllamaCaller()
const p = (xs: number[], q: number) => xs.slice().sort((a, b) => a - b)[Math.min(xs.length - 1, Math.floor(xs.length * q))] ?? 0

console.log('| model | reasoning | p50 | p95 | guard rejections | reasoning chars (avg) |')
console.log('|---|---|---|---|---|---|')
for (const [model, reasoning] of MATRIX) {
  const lat: number[] = []; let rejected = 0; let reasoningTotal = 0
  const rows: string[] = []
  for (const raw of BENCH_FIXTURES) {
    const r = await refineText({ raw, dictionary: [{ term: 'Miraside', replacement: null }, { term: 'Convex', replacement: null }], model, reasoning, timeoutMs: 15000 }, llm)
    lat.push(r.llmMs); reasoningTotal += r.reasoningChars
    if (r.fallbackReason) rejected++
    rows.push(`  ${String(r.llmMs).padStart(5)}ms ${(r.fallbackReason ?? 'ok').padEnd(14)} | ${JSON.stringify(raw.slice(0, 48))} → ${JSON.stringify(r.cleaned.slice(0, 80))}`)
  }
  console.log(`| ${model} | ${reasoning} | ${p(lat, 0.5)} ms | ${p(lat, 0.95)} ms | ${rejected}/${BENCH_FIXTURES.length} | ${Math.round(reasoningTotal / BENCH_FIXTURES.length)} |`)
  console.error(`\n=== ${model} ${reasoning} ===\n${rows.join('\n')}`)
}
for (const n of [3, 5]) {
  const t = performance.now()
  const rs = await Promise.all(BENCH_FIXTURES.slice(0, n).map((raw) => refineText({ raw, dictionary: [], model: MATRIX[0]![0], reasoning: MATRIX[0]![1], timeoutMs: 15000 }, llm)))
  console.log(`\nconcurrency ${n}: wall ${Math.round(performance.now() - t)} ms, failures ${rs.filter((r) => r.fallbackReason).length}`)
}

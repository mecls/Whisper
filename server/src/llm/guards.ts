import { FILLERS } from './fillers.js'

const words = (s: string): string[] => s.toLowerCase().replace(/[^\p{L}\p{N}\s]/gu, ' ').split(/\s+/).filter(Boolean)

/** Raw under 4 words where every word is a filler: the model is right to return nothing. */
export function isNoise(raw: string): boolean {
  const w = words(raw)
  return w.length < 4 && w.every((x) => FILLERS.has(x))
}

const PREAMBLE = /^(here('s| is)( the| your)?( cleaned| corrected| revised)?( text| version| transcript)?\s*:?\s*\n?)/i

/** Strip what a rewrite model adds around the answer. Pure; safe to call twice. */
export function normalizeOutput(s: string): string {
  let out = s.trim()
  // Full think block, or a stray closing tag left by a model whose parser was not engaged.
  out = out.replace(/<think>[\s\S]*?<\/think>/g, '')
  const close = out.lastIndexOf('</think>')
  if (close >= 0) out = out.slice(close + '</think>'.length)
  out = out.trim()
  const fence = out.match(/^```[a-z]*\n([\s\S]*?)\n```$/)
  if (fence) out = fence[1]!.trim()
  if (out.length >= 2 && ((out.startsWith('"') && out.endsWith('"')) || (out.startsWith('“') && out.endsWith('”')))) {
    out = out.slice(1, -1).trim()
  }
  out = out.replace(PREAMBLE, '').trim()
  return out
}

export type GuardResult = { ok: true } | { ok: false; reason: 'empty' | 'too-short' | 'too-long' | 'preamble' | 'think-leak' }

/** Invariants for "a rewrite, not an answer". Ratios from docs/PLAN.md §4.3. */
export function checkGuards(raw: string, out: string): GuardResult {
  if (/<think>/i.test(out)) return { ok: false, reason: 'think-leak' }
  if (out.length === 0) return { ok: false, reason: 'empty' }
  if (/^\s*cleaned text\s*:/im.test(out)) return { ok: false, reason: 'preamble' }
  const rawLen = raw.trim().length
  if (out.length > 2.5 * rawLen + 20) return { ok: false, reason: 'too-long' }
  if (rawLen >= 25 && out.length < 0.3 * rawLen - 12) return { ok: false, reason: 'too-short' }
  return { ok: true }
}

export function estimateTokens(s: string): number {
  return Math.max(1, Math.round(s.length / 3.5))
}

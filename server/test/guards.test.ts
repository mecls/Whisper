import { test } from 'node:test'
import assert from 'node:assert/strict'
import { isNoise, normalizeOutput, checkGuards, estimateTokens } from '../src/llm/guards.js'

test('noise is fewer than 4 words, all fillers', () => {
  assert.equal(isNoise('hã hã'), true)
  assert.equal(isNoise('um uh hmm'), true)
  assert.equal(isNoise('sim'), false)
  assert.equal(isNoise('hã então olá'), false)
  assert.equal(isNoise(''), true)
})

test('normalizeOutput strips fences, quotes, preambles and think blocks', () => {
  assert.equal(normalizeOutput('```\nOlá.\n```'), 'Olá.')
  assert.equal(normalizeOutput('"Olá."'), 'Olá.')
  assert.equal(normalizeOutput('Here is the cleaned text:\nOlá.'), 'Olá.')
  assert.equal(normalizeOutput('<think>reasoning</think>Olá.'), 'Olá.')
  assert.equal(normalizeOutput('some reasoning</think>Olá.'), 'Olá.')
  assert.equal(normalizeOutput('  Olá.  \n'), 'Olá.')
})

test('checkGuards: short inputs skip the lower bound; long ones enforce it', () => {
  assert.deepEqual(checkGuards('Hã hã então olá', 'Olá.'), { ok: true })
  const raw = 'hã então eu acho que que amanhã vamos vamos fechar o contrato tipo às três da tarde'
  assert.deepEqual(checkGuards(raw, 'Eu acho que amanhã vamos fechar o contrato às três da tarde.'), { ok: true })
  assert.deepEqual(checkGuards(raw, 'Ok.'), { ok: false, reason: 'too-short' })
  assert.deepEqual(checkGuards(raw, raw.repeat(4)), { ok: false, reason: 'too-long' })
  assert.deepEqual(checkGuards(raw, ''), { ok: false, reason: 'empty' })
  assert.deepEqual(checkGuards(raw, 'Cleaned text: Eu acho que amanhã vamos fechar o contrato às três da tarde.'), { ok: false, reason: 'preamble' })
  assert.deepEqual(checkGuards(raw, '<think>still here Eu acho que amanhã vamos fechar o contrato às três da tarde.'), { ok: false, reason: 'think-leak' })
})

test('estimateTokens is ~chars/3.5 with a floor of 1', () => {
  assert.equal(estimateTokens(''), 1)
  assert.equal(estimateTokens('a'.repeat(35)), 10)
})

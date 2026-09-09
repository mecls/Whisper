import { test } from 'node:test'
import assert from 'node:assert/strict'
import { Semaphore } from '../src/llm/semaphore.js'

test('fails fast at capacity and releases exactly once', () => {
  const s = new Semaphore(2)
  const a = s.tryAcquire(); const b = s.tryAcquire()
  assert.ok(a && b)
  assert.equal(s.tryAcquire(), null)
  a!(); a!() // double release is a no-op
  assert.equal(s.active, 1)
  assert.ok(s.tryAcquire())
  assert.equal(s.tryAcquire(), null)
  b!()
  assert.equal(s.active, 1)
})

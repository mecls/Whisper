/** Fail-fast counting semaphore: Ollama Cloud queues over-cap requests and we would rather paste raw than wait. */
export class Semaphore {
  #active = 0
  constructor(private readonly max: number) {}
  get active(): number { return this.#active }
  tryAcquire(): (() => void) | null {
    if (this.#active >= this.max) return null
    this.#active++
    let released = false
    return () => {
      if (released) return
      released = true
      this.#active--
    }
  }
}

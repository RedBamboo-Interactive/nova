export interface InvalidationTimer {
  setTimeout(callback: () => void, delayMs: number): number
  clearTimeout(handle: number): void
}

/**
 * Collapse a burst of opaque invalidations into one delayed recovery per key.
 * The callback reads current state when it runs, so superseded payloads never
 * need to be retained and no confidential data enters this coordinator.
 */
export class DeferredInvalidationCoordinator<Key> {
  private readonly pending = new Map<Key, number>()
  private readonly timer: InvalidationTimer
  private readonly delayMs: number

  constructor(timer: InvalidationTimer, delayMs: number) {
    this.timer = timer
    this.delayMs = delayMs
  }

  schedule(key: Key, recover: () => void): void {
    if (this.pending.has(key)) return
    const handle = this.timer.setTimeout(() => {
      this.pending.delete(key)
      recover()
    }, this.delayMs)
    this.pending.set(key, handle)
  }

  cancel(key: Key): void {
    const handle = this.pending.get(key)
    if (handle === undefined) return
    this.timer.clearTimeout(handle)
    this.pending.delete(key)
  }

  clear(): void {
    for (const handle of this.pending.values()) this.timer.clearTimeout(handle)
    this.pending.clear()
  }
}

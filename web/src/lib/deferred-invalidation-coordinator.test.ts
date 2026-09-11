import assert from "node:assert/strict"
import test from "node:test"
import { DeferredInvalidationCoordinator, type InvalidationTimer } from "./deferred-invalidation-coordinator.ts"

class FakeTimer implements InvalidationTimer {
  private nextHandle = 1
  readonly callbacks = new Map<number, () => void>()

  setTimeout(callback: () => void): number {
    const handle = this.nextHandle++
    this.callbacks.set(handle, callback)
    return handle
  }

  clearTimeout(handle: number): void {
    this.callbacks.delete(handle)
  }

  flush(): void {
    const pending = [...this.callbacks.entries()]
    this.callbacks.clear()
    for (const [, callback] of pending) callback()
  }
}

test("coalesces a burst into one delayed invalidation recovery", () => {
  const timer = new FakeTimer()
  const coordinator = new DeferredInvalidationCoordinator<string>(timer, 750)
  const recovered: string[] = []
  coordinator.schedule("discussion-a", () => recovered.push("first"))
  coordinator.schedule("discussion-a", () => recovered.push("duplicate"))
  assert.equal(timer.callbacks.size, 1)
  timer.flush()
  assert.deepEqual(recovered, ["first"])
  coordinator.schedule("discussion-a", () => recovered.push("next window"))
  timer.flush()
  assert.deepEqual(recovered, ["first", "next window"])
})

test("keeps discussions independent and cancels pending recovery", () => {
  const timer = new FakeTimer()
  const coordinator = new DeferredInvalidationCoordinator<string>(timer, 750)
  const recovered: string[] = []
  coordinator.schedule("discussion-a", () => recovered.push("a"))
  coordinator.schedule("discussion-b", () => recovered.push("b"))
  coordinator.cancel("discussion-a")
  assert.equal(timer.callbacks.size, 1)
  coordinator.clear()
  timer.flush()
  assert.deepEqual(recovered, [])
})

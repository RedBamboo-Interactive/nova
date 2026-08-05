import assert from "node:assert/strict"
import test from "node:test"
import {
  DEFAULT_FLOATING_WINDOW_BOUNDS,
  FLOATING_WINDOW_BOUNDS_KEY,
  readFloatingWindowBounds,
  writeFloatingWindowBounds,
  type BoundsStorage,
} from "./floating-window-bounds.ts"

class MemoryStorage implements BoundsStorage {
  values = new Map<string, string>()
  getItem(key: string) { return this.values.get(key) ?? null }
  setItem(key: string, value: string) { this.values.set(key, value) }
}

test("Float Nova defaults invalid or missing persisted dimensions", () => {
  const storage = new MemoryStorage()
  assert.deepEqual(readFloatingWindowBounds(storage), DEFAULT_FLOATING_WINDOW_BOUNDS)
  storage.setItem(FLOATING_WINDOW_BOUNDS_KEY, '{"width":0,"height":700}')
  assert.deepEqual(readFloatingWindowBounds(storage), DEFAULT_FLOATING_WINDOW_BOUNDS)
})

test("Float Nova records and restores the last content dimensions", () => {
  const storage = new MemoryStorage()
  writeFloatingWindowBounds(storage, { innerWidth: 633.6, innerHeight: 911.2 })
  assert.deepEqual(readFloatingWindowBounds(storage), { width: 634, height: 911 })
})

import test from "node:test"
import assert from "node:assert/strict"
import { PendingVisibleContextStore, type PendingVisibleContextEntry } from "./pending-visible-context-store.ts"

function entry(app: string, discarded: string[]): PendingVisibleContextEntry {
  return {
    context: { app, url: `/${app}` },
    discard: () => discarded.push(app),
  }
}

test("pending visible context is isolated by discussion", () => {
  const discarded: string[] = []
  const store = new PendingVisibleContextStore()
  store.set("a", entry("CodeRed", discarded))
  store.set("b", entry("RedLeaf", discarded))

  assert.equal(store.get("a")?.context.app, "CodeRed")
  assert.equal(store.get("b")?.context.app, "RedLeaf")
  assert.equal(store.get("missing"), null)
  assert.deepEqual(discarded, [])
})

test("consume transfers attachment ownership without discarding it", () => {
  const discarded: string[] = []
  const store = new PendingVisibleContextStore()
  store.set("a", entry("CodeRed", discarded))

  assert.equal(store.consume("a")?.context.app, "CodeRed")
  assert.equal(store.get("a"), null)
  assert.deepEqual(discarded, [])
})

test("replace, dismiss, and runtime disposal clean abandoned captures", () => {
  const discarded: string[] = []
  const store = new PendingVisibleContextStore()
  store.set("a", entry("first", discarded))
  store.set("a", entry("second", discarded))
  store.set("b", entry("third", discarded))
  assert.deepEqual(discarded, ["first"])

  store.clear("a")
  assert.deepEqual(discarded, ["first", "second"])
  store.dispose()
  assert.deepEqual(discarded, ["first", "second", "third"])
})

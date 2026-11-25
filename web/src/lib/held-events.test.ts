import { test } from "node:test"
import assert from "node:assert/strict"
import { heldFromOverlays, withHeld, withoutHeld, type HeldEvents } from "./held-events.ts"

test("a history page marks its held overlays and clears the ones that joined a turn", () => {
  let held: HeldEvents = withHeld({}, "d1", "joined")
  held = heldFromOverlays(held, "d1", [
    { messageUid: "waiting", held: true },
    { messageUid: "joined", held: null },
    { messageUid: null, held: true },
  ])
  assert.deepEqual([...held.d1], ["waiting"])
})

test("a live arrival holds, a release lets go, and other discussions are untouched", () => {
  let held: HeldEvents = withHeld({}, "d1", "a")
  held = withHeld(held, "d2", "b")
  held = withoutHeld(held, "d1", "a")
  assert.equal(held.d1.size, 0)
  assert.deepEqual([...held.d2], ["b"])
})

test("nothing changed keeps the same object, so nothing re-renders", () => {
  const held: HeldEvents = withHeld({}, "d1", "a")
  assert.equal(withHeld(held, "d1", "a"), held)
  assert.equal(withoutHeld(held, "d1", "missing"), held)
  assert.equal(heldFromOverlays(held, "d1", [{ messageUid: "a", held: true }]), held)
})

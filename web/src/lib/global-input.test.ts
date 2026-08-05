import assert from "node:assert/strict"
import test from "node:test"
import { parseGlobalInputEvent } from "./global-input.ts"

test("parses a leased native input event", () => {
  assert.deepEqual(parseGlobalInputEvent({ key: "F13", pressed: true, leaseIds: ["lease-1"] }), {
    key: "F13",
    pressed: true,
    leaseIds: ["lease-1"],
  })
})

test("rejects native input events without an explicit recipient lease", () => {
  assert.equal(parseGlobalInputEvent({ key: "F13", pressed: true }), null)
  assert.equal(parseGlobalInputEvent({ key: "F13", pressed: "yes", leaseIds: ["lease-1"] }), null)
  assert.equal(parseGlobalInputEvent({ key: "F13", pressed: false, leaseIds: [12] }), null)
})

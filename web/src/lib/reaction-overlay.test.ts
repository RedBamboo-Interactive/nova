import test from "node:test"
import assert from "node:assert/strict"
import {
  overlayReactions, pendingKey, settlePending, PENDING_ACTOR,
  type ReactionMap,
} from "./reaction-overlay.ts"

const laurent = { id: "u1", name: "Laurent", type: "user" as const }
const nova = { id: "a1", name: "Nova", type: "agent" as const }

test("a click shows at once, before the server confirms it", () => {
  const base: ReactionMap = {}
  const shown = overlayReactions(base, { [pendingKey("m1", "❤️")]: "add" })
  assert.deepEqual(shown.m1, [{ emoji: "❤️", count: 1, actors: [PENDING_ACTOR], userReacted: true }])
  assert.deepEqual(base, {}, "the base map is not mutated")
})

test("adding to an emoji Nova already used counts both", () => {
  const base: ReactionMap = { m1: [{ emoji: "👍", count: 1, actors: [nova], userReacted: false }] }
  const [group] = overlayReactions(base, { [pendingKey("m1", "👍")]: "add" }).m1!
  assert.equal(group!.count, 2)
  assert.equal(group!.userReacted, true)
})

test("removing hides the person's reaction and keeps Nova's", () => {
  const base: ReactionMap = { m1: [{ emoji: "👍", count: 2, actors: [nova, laurent], userReacted: true }] }
  const [group] = overlayReactions(base, { [pendingKey("m1", "👍")]: "remove" }).m1!
  assert.deepEqual(group, { emoji: "👍", count: 1, actors: [nova], userReacted: false })
})

test("removing the only reaction removes the pill", () => {
  const base: ReactionMap = { m1: [{ emoji: "❤️", count: 1, actors: [laurent], userReacted: true }] }
  assert.deepEqual(overlayReactions(base, { [pendingKey("m1", "❤️")]: "remove" }).m1, [])
})

test("the server's confirmation settles a pending click; an unconfirmed one stays", () => {
  const pending = { [pendingKey("m1", "❤️")]: "add" as const, [pendingKey("m2", "🔥")]: "add" as const }
  const base: ReactionMap = { m1: [{ emoji: "❤️", count: 1, actors: [laurent], userReacted: true }] }
  assert.deepEqual(settlePending(base, pending), { [pendingKey("m2", "🔥")]: "add" })
  assert.deepEqual(overlayReactions(base, settlePending(base, pending)).m1, base.m1, "no double count")
})

test("a confirmed remove settles too, and nothing pending returns the same objects", () => {
  const pending = { [pendingKey("m1", "❤️")]: "remove" as const }
  assert.deepEqual(settlePending({}, pending), {})
  const base: ReactionMap = { m1: [] }
  assert.equal(overlayReactions(base, {}), base)
  const still = { [pendingKey("m1", "❤️")]: "add" as const }
  assert.equal(settlePending(base, still), still)
})

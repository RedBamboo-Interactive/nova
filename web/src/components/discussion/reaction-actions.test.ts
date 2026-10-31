import assert from "node:assert/strict"
import { test } from "node:test"
import { reactionMenuEntries } from "./reaction-actions.ts"

test("React lists the picker's reactions in order and adds the chosen one", () => {
  const added: string[] = []
  const [react, ...rest] = reactionMenuEntries([
    { emoji: "👍", icon: "ph-light ph-thumbs-up" },
    { emoji: "🔥", icon: "ph-bold ph-fire" },
  ], emoji => added.push(emoji))
  assert.equal(rest.length, 0)
  assert.ok(!("separator" in react))
  assert.equal(react.label, "React")
  const children = react.children ?? []
  assert.deepEqual(children.map(child => ("label" in child ? child.label : "")), ["👍", "🔥"])
  assert.deepEqual(children.map(child => ("icon" in child ? child.icon : "")), ["ph-light ph-thumbs-up", "ph-bold ph-fire"])
  const fire = children[1]
  if ("run" in fire) void fire.run?.({} as never)
  assert.deepEqual(added, ["🔥"])
})

test("no React item without reactions to offer", () => {
  assert.deepEqual(reactionMenuEntries([], () => {}), [])
})

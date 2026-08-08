import { test } from "node:test"
import assert from "node:assert/strict"
import { resolveRotatedDiscussionSelection } from "./discussion-rotation.ts"

test("follows the replacement when the rotated discussion was selected", () => {
  assert.equal(resolveRotatedDiscussionSelection("live-old", "live-old", "live-new"), "live-new")
})

test("preserves another selected discussion during rotation", () => {
  assert.equal(resolveRotatedDiscussionSelection("chat", "live-old", "live-new"), "chat")
  assert.equal(resolveRotatedDiscussionSelection(null, "live-old", "live-new"), null)
})

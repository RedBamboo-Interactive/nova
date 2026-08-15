import assert from "node:assert/strict"
import test from "node:test"
import { isDiscussionSelectionCurrent } from "./discussion-view-selection.ts"

test("does not expose the previous transcript while a requested discussion selection catches up", () => {
  assert.equal(isDiscussionSelectionCurrent("new-discussion", "old-discussion"), false)
  assert.equal(isDiscussionSelectionCurrent("new-discussion", "new-discussion"), true)
})

test("allows the locally selected discussion when the route does not request one", () => {
  assert.equal(isDiscussionSelectionCurrent(null, "live-discussion"), true)
})

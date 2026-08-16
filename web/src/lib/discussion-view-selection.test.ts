import assert from "node:assert/strict"
import test from "node:test"
import {
  isDiscussionSelectionCurrent,
  resolveRequestedDiscussionId,
} from "./discussion-view-selection.ts"

test("does not expose the previous transcript while a requested discussion selection catches up", () => {
  assert.equal(isDiscussionSelectionCurrent("new-discussion", "old-discussion"), false)
  assert.equal(isDiscussionSelectionCurrent("new-discussion", "new-discussion"), true)
})

test("allows the locally selected discussion when the route does not request one", () => {
  assert.equal(isDiscussionSelectionCurrent(null, "live-discussion"), true)
})

test("a clicked discussion masks the old route until navigation catches up", () => {
  const requested = resolveRequestedDiscussionId("old-discussion", "new-discussion")

  assert.equal(requested, "new-discussion")
  assert.equal(isDiscussionSelectionCurrent(requested, "old-discussion"), false)
})

test("route selection resumes authority after the pending handoff clears", () => {
  assert.equal(resolveRequestedDiscussionId("new-discussion", null), "new-discussion")
})

test("a deep link wins over the default LIVE discussion", () => {
  assert.equal(resolveRequestedDiscussionId("chat-a", null, "live"), "chat-a")
})

test("the default discussion is used only when no route or handoff selects one", () => {
  assert.equal(resolveRequestedDiscussionId(null, null, "live"), "live")
  assert.equal(resolveRequestedDiscussionId(null, "chat-a", "live"), "chat-a")
})

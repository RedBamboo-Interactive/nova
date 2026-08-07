import assert from "node:assert/strict"
import test from "node:test"
import {
  discussionIdFromNovaChatPath,
  resolveFloatingOpenSelection,
} from "./floating-open-selection.ts"

test("a closed Float starts from Normal Nova's current discussion", () => {
  assert.equal(resolveFloatingOpenSelection({
    openerPathname: "/apps/nova/chat/current-discussion",
    persistedDiscussionId: "old-float-discussion",
    surfaceAlreadyOpen: false,
    surfaceOpening: false,
  }), "current-discussion")
})

test("an explicit opening target wins over the current Nova route", () => {
  assert.equal(resolveFloatingOpenSelection({
    explicitDiscussionId: "explicit-discussion",
    openerPathname: "/apps/nova/chat/current-discussion",
    persistedDiscussionId: "old-float-discussion",
    surfaceAlreadyOpen: false,
    surfaceOpening: false,
  }), "explicit-discussion")
})

test("opening or open Float keeps its independent selection", () => {
  for (const [surfaceAlreadyOpen, surfaceOpening] of [[true, false], [false, true]]) {
    assert.equal(resolveFloatingOpenSelection({
      explicitDiscussionId: "normal-discussion",
      openerPathname: "/apps/nova/chat/normal-discussion",
      persistedDiscussionId: "float-discussion",
      surfaceAlreadyOpen,
      surfaceOpening,
    }), "float-discussion")
  }
})

test("opening outside Nova restores Float's own last selection", () => {
  assert.equal(resolveFloatingOpenSelection({
    openerPathname: "/apps/codered",
    persistedDiscussionId: "float-discussion",
    surfaceAlreadyOpen: false,
    surfaceOpening: false,
  }), "float-discussion")
})

test("Nova chat root and malformed escapes do not invent a discussion", () => {
  assert.equal(discussionIdFromNovaChatPath("/apps/nova/chat"), null)
  assert.equal(discussionIdFromNovaChatPath("/apps/nova/chat/%E0%A4%A"), null)
})

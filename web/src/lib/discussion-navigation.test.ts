import { test } from "node:test"
import assert from "node:assert/strict"
import { getAdjacentSidebarDiscussion, getSidebarDiscussionOrder } from "./discussion-navigation.ts"
import type { DiscussionInfo } from "./types.ts"

function discussion(id: string, type: DiscussionInfo["type"], agentId = "agent-a"): DiscussionInfo {
  return {
    id,
    entityId: id,
    title: id,
    sessionId: null,
    status: "idle",
    type,
    createdAt: "2026-08-01T00:00:00.000Z",
    lastActivity: "2026-08-01T00:00:00.000Z",
    messageCount: 0,
    lastReadAt: null,
    agentId,
  }
}

test("matches the visible sidebar order", () => {
  const discussions = [
    discussion("new-chat", "chat"),
    discussion("heartbeat", "heartbeat"),
    discussion("live", "live"),
    discussion("old-chat", "chat"),
  ]

  assert.deepEqual(
    getSidebarDiscussionOrder(discussions, null).map((item) => item.id),
    ["live", "new-chat", "old-chat"],
  )
})

test("respects the sidebar agent filter", () => {
  const discussions = [
    discussion("agent-b-live", "live", "agent-b"),
    discussion("agent-a-chat", "chat"),
    discussion("agent-b-chat", "chat", "agent-b"),
  ]

  assert.deepEqual(
    getSidebarDiscussionOrder(discussions, "agent-b").map((item) => item.id),
    ["agent-b-live", "agent-b-chat"],
  )
})

test("Alt+Down skips Heartbeat and follows visible rows", () => {
  const discussions = [
    discussion("first-chat", "chat"),
    discussion("heartbeat", "heartbeat"),
    discussion("second-chat", "chat"),
    discussion("live", "live"),
  ]

  assert.equal(getAdjacentSidebarDiscussion(discussions, "first-chat", 1, null)?.id, "second-chat")
  assert.equal(getAdjacentSidebarDiscussion(discussions, "second-chat", -1, null)?.id, "first-chat")
})

test("treats Heartbeat as its visible Live row", () => {
  const discussions = [
    discussion("first-chat", "chat"),
    discussion("heartbeat", "heartbeat"),
    discussion("live", "live"),
  ]

  assert.equal(getAdjacentSidebarDiscussion(discussions, "heartbeat", 1, null)?.id, "first-chat")
  assert.equal(getAdjacentSidebarDiscussion(discussions, "heartbeat", -1, null)?.id, "first-chat")
})

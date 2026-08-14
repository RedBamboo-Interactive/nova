import { test } from "node:test"
import assert from "node:assert/strict"
import { applySessionStatus, discussionStatusForSession, preservesRecentStreamingLatch } from "./discussion-runtime.ts"
import type { DiscussionInfo } from "./types.ts"

function discussion(status: DiscussionInfo["status"] = "idle", type: DiscussionInfo["type"] = "chat"): DiscussionInfo {
  return {
    id: "discussion-a",
    entityId: "entity-a",
    title: "A",
    sessionId: "session-a",
    status,
    type,
    createdAt: "2026-08-07T00:00:00.000Z",
    lastActivity: "2026-08-07T00:00:00.000Z",
    messageCount: 1,
    lastReadAt: null,
    agentId: "agent-a",
  }
}

test("an active compute session makes the discussion indicator active", () => {
  const [updated] = applySessionStatus([discussion("idle")], "discussion-a", "Active")
  assert.equal(updated?.status, "thinking")
})

test("an idle compute session clears the active discussion indicator", () => {
  const [updated] = applySessionStatus([discussion("thinking")], "discussion-a", "Idle")
  assert.equal(updated?.status, "idle")
})

test("closed discussions ignore late session events", () => {
  const archived = discussion("archived")
  assert.equal(applySessionStatus([archived], "discussion-a", "Active")[0], archived)
})

test("a stopped LIVE session stays available while an ordinary chat stops", () => {
  assert.equal(discussionStatusForSession("Stopped", "live"), "idle")
  assert.equal(discussionStatusForSession("Stopped", "chat"), "stopped")
})

test("a transient idle event cannot clear a freshly submitted turn", () => {
  assert.equal(preservesRecentStreamingLatch("Idle", true, 1_000, 1_500, 10_000), true)
  assert.equal(preservesRecentStreamingLatch("Starting", true, 1_000, 1_500, 10_000), true)
  assert.equal(preservesRecentStreamingLatch("Idle", true, 1_000, 11_001, 10_000), false)
  assert.equal(preservesRecentStreamingLatch("Stopped", true, 1_000, 1_500, 10_000), false)
  assert.equal(preservesRecentStreamingLatch("Idle", false, 1_000, 1_500, 10_000), false)
})

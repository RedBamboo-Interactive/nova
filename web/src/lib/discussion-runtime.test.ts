import { test } from "node:test"
import assert from "node:assert/strict"
import { applySessionStatus, applySettledSessionStatus, discussionStatusForSession, preservesRecentStreamingLatch, sendNeedsExplicitResume, shouldRequestSessionTitleSync } from "./discussion-runtime.ts"
import type { DiscussionInfo } from "./types.ts"

function discussion(status: DiscussionInfo["status"] = "idle", type: DiscussionInfo["type"] = "chat"): DiscussionInfo {
  return {
    id: "discussion-a",
    entityId: "entity-a",
    title: "A",
    sessionId: "session-a",
    titleSource: null,
    status,
    type,
    createdAt: "2026-08-07T00:00:00.000Z",
    lastActivity: "2026-08-07T00:00:00.000Z",
    messageCount: 1,
    lastReadAt: null,
    conversationRevision: 0,
    readConversationRevision: 0,
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

test("an inactive ordinary discussion still requests server-authoritative title refinement", () => {
  assert.equal(shouldRequestSessionTitleSync(discussion("idle", "chat"), "Refined"), true)
})

test("standing discussion titles never request session refinement", () => {
  assert.equal(shouldRequestSessionTitleSync(discussion("idle", "live"), "Drift"), false)
  assert.equal(shouldRequestSessionTitleSync(discussion("idle", "heartbeat"), "Drift"), false)
  assert.equal(shouldRequestSessionTitleSync(discussion("idle", "chat"), "  "), false)
})

test("a settled session event clears the active indicator without a refresh", () => {
  const settledAt = "2026-08-07T00:01:00.000Z"
  const [updated] = applySettledSessionStatus(
    [discussion("thinking")],
    "discussion-a",
    "Idle",
    settledAt,
  )
  assert.equal(updated?.status, "idle")
  assert.equal(updated?.lastActivity, settledAt)
  assert.equal(updated?.lastReadAt, null)
})

test("a stopped session settles without marking the discussion read", () => {
  const original = discussion("thinking")
  const [updated] = applySettledSessionStatus(
    [{ ...original, lastReadAt: "2026-08-07T00:00:30.000Z" }],
    "discussion-a",
    "Stopped",
    "2026-08-07T00:01:00.000Z",
  )
  assert.equal(updated?.status, "stopped")
  assert.equal(updated?.lastReadAt, "2026-08-07T00:00:30.000Z")
})

test("closed discussions ignore late session events", () => {
  const archived = discussion("archived")
  assert.equal(applySessionStatus([archived], "discussion-a", "Active")[0], archived)
})

test("a stopped session requires recovery for LIVE and ordinary chats", () => {
  assert.equal(discussionStatusForSession("Stopped", "live"), "stopped")
  assert.equal(discussionStatusForSession("Stopped", "chat"), "stopped")
})

test("a planned or crash restart remains visibly resumable", () => {
  assert.equal(discussionStatusForSession("Stopped", "chat", "maintenance_restart"), "idle")
  assert.equal(discussionStatusForSession("Stopped", "chat", "orphaned_on_restart"), "idle")
  assert.equal(discussionStatusForSession("Stopped", "chat", "user_stopped"), "stopped")
})

test("a transient idle event cannot clear a freshly submitted turn", () => {
  assert.equal(preservesRecentStreamingLatch("Idle", true, 1_000, 1_500, 10_000), true)
  assert.equal(preservesRecentStreamingLatch("Starting", true, 1_000, 1_500, 10_000), true)
  assert.equal(preservesRecentStreamingLatch("Idle", true, 1_000, 11_001, 10_000), false)
  assert.equal(preservesRecentStreamingLatch("Stopped", true, 1_000, 1_500, 10_000), false)
  assert.equal(preservesRecentStreamingLatch("Idle", false, 1_000, 1_500, 10_000), false)
})

test("idle settles immediately after Active was observed for the submitted turn", () => {
  assert.equal(
    preservesRecentStreamingLatch("Idle", true, 1_000, 1_500, 10_000, true),
    false,
  )
})

// Any session.stream event used to latch isStreaming and force the discussion
// Active, so a background subagent's calls brought back "Reading..." and Stop
// over an idle session (Chat Stability Journal cfb5b994).
test("background stream events never touch the turn state", async () => {
  const { sessionStreamEffects } = await import("./discussion-runtime.ts")
  const streaming = { isStreaming: true, resumePending: false }
  for (const event of [
    { type: "tool_use" as const, parentToolUseId: "toolu_agent" },
    { type: "tool_result" as const, parentToolUseId: "toolu_agent" },
    { type: "task" as const },
  ]) {
    assert.deepEqual(sessionStreamEffects(event, streaming), { applyTurnState: false, settleIdle: false, forceActive: false })
    assert.deepEqual(sessionStreamEffects({ type: "text" }, { ...streaming, background: true }).applyTurnState, false)
  }
  assert.deepEqual(sessionStreamEffects({ type: "tool_use" }, streaming), { applyTurnState: true, settleIdle: false, forceActive: true })
  assert.deepEqual(sessionStreamEffects({ type: "status", content: "idle" }, { isStreaming: false, resumePending: false }),
    { applyTurnState: true, settleIdle: true, forceActive: false })
  assert.deepEqual(sessionStreamEffects({ type: "status", content: "killed" }, { isStreaming: false, resumePending: true }),
    { applyTurnState: true, settleIdle: false, forceActive: true })
})

test("every non-restart stop projects to the one grey stopped state", () => {
  for (const reason of ["user_stopped", "idle_timeout", "idle_ttl_expired", "usage_limit", undefined])
    assert.equal(discussionStatusForSession("Stopped", "chat", reason), "stopped")
  assert.equal(discussionStatusForSession("Error", "chat"), "stopped")
})

test("a send resumes explicitly unless the session runs or RedCompute auto-resumes it", () => {
  for (const reason of ["maintenance_restart", "orphaned_on_restart", "idle_timeout", "idle_ttl_expired"])
    assert.equal(sendNeedsExplicitResume("Stopped", reason), false)
  for (const reason of ["user_stopped", "usage_limit", "dismissed", null, undefined])
    assert.equal(sendNeedsExplicitResume("Stopped", reason), true)
  assert.equal(sendNeedsExplicitResume("Error", null), true)
  assert.equal(sendNeedsExplicitResume("Error", "idle_timeout"), false)
  for (const status of ["Active", "Idle", "Starting"])
    assert.equal(sendNeedsExplicitResume(status, null), false)
})

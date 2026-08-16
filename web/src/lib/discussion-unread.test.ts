import { test } from "node:test"
import assert from "node:assert/strict"
import { applyConversationMessageArrival, applyDiscussionMessageArrival, isDiscussionUnread } from "./discussion-unread.ts"
import type { DiscussionInfo } from "./types.ts"

function discussion(overrides: Partial<DiscussionInfo> = {}): DiscussionInfo {
  return {
    id: "live",
    entityId: "live-entity",
    title: null,
    sessionId: "live-session",
    status: "idle",
    type: "live",
    createdAt: "2026-08-16T09:00:00.000Z",
    lastActivity: "2026-08-16T09:01:00.000Z",
    messageCount: 1,
    lastReadAt: "2026-08-16T09:00:30.000Z",
    conversationRevision: 1,
    readConversationRevision: 0,
    agentId: "nova",
    ...overrides,
  }
}

test("LIVE discussions use the same unread contract as ordinary chats", () => {
  assert.equal(isDiscussionUnread(discussion()), true)
  assert.equal(isDiscussionUnread(discussion({ type: "chat" })), true)
  assert.equal(isDiscussionUnread(discussion({ readConversationRevision: 1 })), false)
})

test("heartbeat discussions never advertise unread messages", () => {
  assert.equal(isDiscussionUnread(discussion({ type: "heartbeat" })), false)
})

test("ambient pushed activity never makes the discussion unread", () => {
  const [updated] = applyDiscussionMessageArrival(
    [discussion({ messageCount: 0, conversationRevision: 0, readConversationRevision: 0 })],
    "live",
    "2026-08-16T09:03:00.000Z",
  )

  assert.equal(updated?.messageCount, 1)
  assert.equal(updated?.lastActivity, "2026-08-16T09:03:00.000Z")
  assert.equal(updated?.conversationRevision, 0)
  assert.equal(isDiscussionUnread(updated!), false)
})

test("projecting the same arrival from two mounted surfaces is idempotent", () => {
  const timestamp = "2026-08-16T09:03:00.000Z"
  const once = applyDiscussionMessageArrival([discussion({ messageCount: 0 })], "live", timestamp)
  const twice = applyDiscussionMessageArrival(once, "live", timestamp)

  assert.equal(twice[0]?.messageCount, 1)
  assert.equal(twice[0]?.lastActivity, timestamp)
})

test("a canonical conversation revision makes an off-screen discussion unread", () => {
  const [updated] = applyConversationMessageArrival(
    [discussion({ conversationRevision: 1, readConversationRevision: 1 })],
    "live",
    "2026-08-16T09:03:00.000Z",
    2,
  )

  assert.equal(updated?.conversationRevision, 2)
  assert.equal(updated?.readConversationRevision, 1)
  assert.equal(isDiscussionUnread(updated!), true)
})

test("duplicate pushed revisions are idempotent", () => {
  const timestamp = "2026-08-16T09:03:00.000Z"
  const once = applyConversationMessageArrival([discussion()], "live", timestamp, 2)
  const twice = applyConversationMessageArrival(once, "live", timestamp, 2)

  assert.equal(twice[0]?.conversationRevision, 2)
})

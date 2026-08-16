import { test } from "node:test"
import assert from "node:assert/strict"
import { applyDiscussionMessageArrival, isDiscussionUnread } from "./discussion-unread.ts"
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
    agentId: "nova",
    ...overrides,
  }
}

test("LIVE discussions use the same unread contract as ordinary chats", () => {
  assert.equal(isDiscussionUnread(discussion()), true)
  assert.equal(isDiscussionUnread(discussion({ type: "chat" })), true)
  assert.equal(isDiscussionUnread(discussion({ lastReadAt: "2026-08-16T09:02:00.000Z" })), false)
})

test("heartbeat discussions never advertise unread messages", () => {
  assert.equal(isDiscussionUnread(discussion({ type: "heartbeat" })), false)
})

test("an off-screen pushed message makes the discussion unread", () => {
  const [updated] = applyDiscussionMessageArrival(
    [discussion({ messageCount: 0, lastReadAt: "2026-08-16T09:02:00.000Z" })],
    "live",
    "2026-08-16T09:03:00.000Z",
    null,
  )

  assert.equal(updated?.messageCount, 1)
  assert.equal(updated?.lastActivity, "2026-08-16T09:03:00.000Z")
  assert.equal(updated?.lastReadAt, "2026-08-16T09:02:00.000Z")
  assert.equal(isDiscussionUnread(updated!), true)
})

test("projecting the same arrival from two mounted surfaces is idempotent", () => {
  const timestamp = "2026-08-16T09:03:00.000Z"
  const once = applyDiscussionMessageArrival([discussion({ messageCount: 0 })], "live", timestamp, null)
  const twice = applyDiscussionMessageArrival(once, "live", timestamp, null)

  assert.equal(twice[0]?.messageCount, 1)
  assert.equal(twice[0]?.lastActivity, timestamp)
})

test("a pushed message remains read while its discussion is being viewed", () => {
  const [updated] = applyDiscussionMessageArrival(
    [discussion()],
    "live",
    "2026-08-16T09:03:00.000Z",
    "2026-08-16T09:03:01.000Z",
  )

  assert.equal(updated?.lastReadAt, "2026-08-16T09:03:01.000Z")
  assert.equal(isDiscussionUnread(updated!), false)
})

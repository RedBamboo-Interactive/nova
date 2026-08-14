import { test } from "node:test"
import assert from "node:assert/strict"
import {
  clearDiscussionArchivePending,
  getDiscussionList,
  markDiscussionArchivePending,
  resetDiscussionListStore,
  setDiscussionList,
  subscribeDiscussionList,
  upsertDiscussion,
} from "./discussion-list-store.ts"
import type { DiscussionInfo } from "./types.ts"

function discussion(id: string, title: string | null = id): DiscussionInfo {
  return {
    id,
    entityId: id,
    title,
    sessionId: `${id}-session`,
    status: "idle",
    type: "chat",
    createdAt: "2026-08-05T00:00:00.000Z",
    lastActivity: "2026-08-05T00:00:00.000Z",
    messageCount: 0,
    lastReadAt: null,
    agentId: "agent-a",
  }
}

test("shares discussion mutations with every mounted surface", () => {
  resetDiscussionListStore()
  let normalNotifications = 0
  let floatNotifications = 0
  const unsubscribeNormal = subscribeDiscussionList(() => normalNotifications++)
  const unsubscribeFloat = subscribeDiscussionList(() => floatNotifications++)

  setDiscussionList([discussion("new", null)])
  setDiscussionList((current) => current.map((item) => item.id === "new"
    ? { ...item, title: "Shared title" }
    : item))

  assert.equal(getDiscussionList()[0]?.title, "Shared title")
  assert.equal(normalNotifications, 2)
  assert.equal(floatNotifications, 2)

  unsubscribeNormal()
  unsubscribeFloat()
})

test("an identical server refresh reuses records and publishes nothing", () => {
  resetDiscussionListStore()
  const original = discussion("stable")
  setDiscussionList([original])
  let notifications = 0
  const unsubscribe = subscribeDiscussionList(() => notifications++)

  setDiscussionList([{ ...original }])

  assert.equal(notifications, 0)
  assert.equal(getDiscussionList()[0], original)
  unsubscribe()
})

test("an in-flight archive cannot be resurrected by another surface refresh", () => {
  resetDiscussionListStore()
  markDiscussionArchivePending("archived")
  setDiscussionList([discussion("archived"), discussion("kept")])

  assert.equal(getDiscussionList().find((item) => item.id === "archived")?.status, "archiving")
  assert.equal(getDiscussionList().find((item) => item.id === "kept")?.status, "idle")

  clearDiscussionArchivePending("archived")
  setDiscussionList([discussion("archived")])
  assert.equal(getDiscussionList()[0]?.status, "idle")
})

test("the create response replaces an incomplete websocket placeholder", () => {
  resetDiscussionListStore()
  setDiscussionList([{
    ...discussion("new", null),
    entityId: "",
    sessionId: null,
  }])

  upsertDiscussion({
    ...discussion("new", null),
    entityId: "entity-new",
    sessionId: "session-new",
  })

  assert.equal(getDiscussionList()[0]?.entityId, "entity-new")
  assert.equal(getDiscussionList()[0]?.sessionId, "session-new")
})

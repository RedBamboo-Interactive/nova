import { test } from "node:test"
import assert from "node:assert/strict"
import { discussionLabel, discussionSections, groupingInputKey, groupingStillVisible, type DiscussionGroupingView } from "./discussion-grouping.ts"
import { getAdjacentSidebarDiscussion, getSidebarDiscussionOrder } from "./discussion-navigation.ts"
import type { DiscussionInfo } from "./types.ts"
const make = (id: string, title = id, extra: Partial<DiscussionInfo> = {}): DiscussionInfo => ({ id, entityId: id, title, type: "chat", status: "idle", sessionId: null, agentId: "nova", messageCount: 1, lastReadAt: null, createdAt: "2026-11-19T00:00:00Z", lastActivity: "2026-11-19T00:00:00Z", ...extra })
const rows = [make("triage"), make("hud"), make("icons"), make("roleplay"), make("cloud"), make("live", "Live", { type: "live" }), make("heartbeat", "Heartbeat", { type: "heartbeat" })]
const view: DiscussionGroupingView = { fingerprint: "f", sourceFingerprint: "f", updatedAt: null, retryAfterSeconds: 0, groupOrder: ["Lands of Lore", "Leaf"], assignments: [
  { discussionId: "hud", sourceTitle: "hud", group: "Lands of Lore", label: "Inventory", icon: "layout" },
  { discussionId: "icons", sourceTitle: "icons", group: "Lands of Lore", label: "Icons", icon: "image" },
  { discussionId: "triage", sourceTitle: "triage", group: "Leaf", label: "Grouping", icon: "chats-circle" },
  { discussionId: "cloud", sourceTitle: "cloud", group: "Leaf", label: "Nodes", icon: "cloud" },
] }
test("groups every visible chat once and keeps live outside groups", () => {
  const order = getSidebarDiscussionOrder(rows, null, view).map(d => d.id)
  assert.deepEqual(order, ["live", "hud", "icons", "triage", "cloud", "roleplay"])
  assert.equal(new Set(order).size, order.length)
  assert.deepEqual(getSidebarDiscussionOrder(rows, null).map(d => d.id), ["live", "triage", "hud", "icons", "roleplay", "cloud"])
})
test("keyboard navigation follows grouped visible order", () => {
  assert.equal(getAdjacentSidebarDiscussion(rows, "icons", 1, null, view)?.id, "triage")
  assert.equal(getAdjacentSidebarDiscussion(rows, "hud", -1, null, view)?.id, "live")
  assert.equal(getAdjacentSidebarDiscussion(rows, "roleplay", 1, null, view)?.id, "live")
})
test("new, uncertain and singleton discussions stay visible", () => {
  const subset = [make("new"), rows[1], rows[3]]
  const sections = discussionSections(subset, view)
  assert.equal(sections.length, 1)
  assert.equal(sections[0].name, null)
  assert.deepEqual(sections[0].items.map(d => d.id), ["new", "hud", "roleplay"])
})
test("agent filtering cannot introduce hidden discussion IDs", () => {
  const mixed = rows.map(d => d.id === "icons" ? { ...d, agentId: "another" } : d)
  assert.equal(getSidebarDiscussionOrder(mixed, "nova", view).some(d => d.id === "icons"), false)
})
test("labels never replace canonical titles, confidential or renamed rows", () => {
  assert.equal(discussionLabel(rows[1], view)?.label, "Inventory")
  assert.equal(rows[1].title, "hud")
  assert.equal(discussionLabel({ ...rows[1], confidential: true }, view), undefined)
  assert.equal(discussionLabel({ ...rows[1], title: "New subject" }, view), undefined)
  assert.equal(discussionLabel({ ...rows[1], status: "archiving" }, view), undefined)
})
test("status, read markers and activity do not cause classifier work", () => {
  const key = groupingInputKey(rows)
  assert.equal(groupingInputKey(rows.map(d => ({ ...d, status: "thinking", lastActivity: "2026-11-20", readConversationRevision: 10 }))), key)
  assert.equal(groupingInputKey([...rows].reverse()), key)
  assert.notEqual(groupingInputKey(rows.map(d => d.id === "hud" ? { ...d, title: "Renamed" } : d)), key)
})
test("a removed or newly confidential source invalidates the derived cache", () => {
  assert.equal(groupingStillVisible(view, rows), true)
  assert.equal(groupingStillVisible(view, rows.filter(d => d.id !== "hud")), false)
  assert.equal(groupingStillVisible(view, rows.map(d => d.id === "hud" ? { ...d, confidential: true } : d)), false)
})


test("group visuals are explicit and independent of row activity order", () => {
  const visuals: DiscussionGroupingView = { ...view, groups: [
    { name: "Lands of Lore", icon: "mountains", thumbnailDiscussionId: "icons" },
    { name: "Leaf", icon: "leaf", thumbnailDiscussionId: null },
  ] }
  const groups = discussionSections(rows, visuals)
  assert.equal(groups[0].icon, "mountains")
  assert.equal(groups[0].thumbnailDiscussionId, "icons")
  assert.equal(groups[1].icon, "leaf")
  assert.equal(groups[1].thumbnailDiscussionId, undefined)
  assert.equal(discussionSections([...rows].reverse(), visuals)[0].icon, "mountains")
  assert.equal(discussionSections(rows, view)[0].icon, "chats-circle")
})

test("filtered out or foreign group representatives cannot trigger thumbnail reads", () => {
  const visuals: DiscussionGroupingView = { ...view, groups: [
    { name: "Lands of Lore", icon: "mountains", thumbnailDiscussionId: "cloud" },
  ] }
  assert.equal(discussionSections(rows, visuals)[0].thumbnailDiscussionId, undefined)
})

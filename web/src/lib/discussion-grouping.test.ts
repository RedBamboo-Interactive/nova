import { test } from "node:test"
import assert from "node:assert/strict"
import { discussionLabel, discussionSections, groupingInputKey, groupingStillVisible, moveDiscussionGroup, type DiscussionGroupingSort, type DiscussionGroupSortMode, type DiscussionGroupingView } from "./discussion-grouping.ts"
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
test("one visible discussion keeps its project heading while uncertain rows stay loose", () => {
  const subset = [make("new"), rows[1], rows[3]]
  const sections = discussionSections(subset, view)
  assert.deepEqual(sections.map(section => section.name), ["Lands of Lore", null])
  assert.deepEqual(sections[0].items.map(d => d.id), ["hud"])
  assert.deepEqual(sections[1].items.map(d => d.id), ["new", "roleplay"])
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
test("grouping refreshes through early messages and at later checkpoints", () => {
  const key = (messageCount: number) => groupingInputKey([make("stable", "Stable title", { messageCount })])
  assert.notEqual(key(0), key(1))
  assert.notEqual(key(4), key(5))
  assert.notEqual(key(11), key(12))
  assert.equal(key(12), key(19))
  assert.notEqual(key(19), key(20))
  assert.equal(key(20), key(27))
  assert.notEqual(key(27), key(28))
  assert.notEqual(key(20), groupingInputKey([make("stable", "Stable title", { messageCount: 20, sessionId: "new-session" })]))
})
test("closing a row leaves the other sections visible through the pending archive and undo", () => {
  const closing = rows.map(row => row.id === "hud" ? { ...row, status: "archiving" as const } : row)
  const without = rows.filter(row => row.id !== "hud")
  const locallyClosing = (id: string) => id === "hud"
  assert.equal(groupingStillVisible(view, closing, locallyClosing), true)
  assert.equal(groupingStillVisible(view, without, locallyClosing), true)
  assert.deepEqual(discussionSections(closing, view).map(section => section.name), ["Lands of Lore", "Leaf", null])
  assert.equal(discussionSections(closing, view)[0].items.some(row => row.id === "hud"), false)
  assert.equal(groupingStillVisible(view, rows, locallyClosing), true)
  assert.equal(groupingStillVisible(view, closing.map(row => row.id === "hud" ? { ...row, confidential: true } : row), locallyClosing), false)
})
test("an added or renamed row stays loose while the other categories stay visible", () => {
  const latest = [...rows.map(row => row.id === "hud" ? { ...row, title: "Renamed subject" } : row), make("new")]
  assert.equal(groupingStillVisible(view, latest), true)
  const sections = discussionSections(latest, view)
  assert.deepEqual(sections.map(section => section.name), ["Lands of Lore", "Leaf", null])
  assert.deepEqual(sections[0].items.map(row => row.id), ["icons"])
  assert(sections[2].items.some(row => row.id === "hud"))
  assert(sections[2].items.some(row => row.id === "new"))
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


test("sorts project headings without changing rows, Live, or Ungrouped", () => {
  const dated = rows.map(row => row.id === "cloud" ? { ...row, lastActivity: "2026-11-21T00:00:00Z" } : row)
  const groups = (mode: DiscussionGroupSortMode, manualOrder: string[] = []) =>
    discussionSections(dated, view, { mode, manualOrder }).map(section => [section.name, section.items.map(row => row.id)])

  const lands = ["Lands of Lore", ["hud", "icons"]]
  const leaf = ["Leaf", ["triage", "cloud"]]
  assert.deepEqual(groups("activity").slice(0, 2), [leaf, lands])
  assert.deepEqual(groups("alphabetical").slice(0, 2), [lands, leaf])
  assert.deepEqual(groups("manual", ["Leaf", "Unavailable"]).slice(0, 2), [leaf, lands])

  const sort = { mode: "activity", manualOrder: [] } as const
  assert.deepEqual(getSidebarDiscussionOrder(dated, null, view, [], sort).map(row => row.id),
    ["live", "triage", "cloud", "hud", "icons", "roleplay"])
  assert.equal(getAdjacentSidebarDiscussion(dated, "cloud", 1, null, view, [], sort)?.id, "hud")
  assert.equal(getAdjacentSidebarDiscussion(dated, "cloud", 1, null, view, ["Lands of Lore"], sort)?.id, "roleplay")

  const updates = dated.map(row => row.id === "icons" ? { ...row, lastActivity: "2026-11-22T00:00:00Z" } : row)
  assert.equal(discussionSections(updates, view, sort)[0].name, "Lands of Lore")
  const scoped = dated.filter(row => row.id !== "triage")
  assert.equal(discussionSections(scoped, view, { mode: "size", manualOrder: [] })[0].name, "Lands of Lore")
})

test("manual reorder keeps filtered and temporarily absent projects in their saved slots", () => {
  const order = ["First", "Filtered", "Second", "Temporarily absent", "Third"]
  const visible = ["First", "Second", "Third"]
  assert.deepEqual(moveDiscussionGroup(order, visible, "Third", "First"),
    ["Third", "Filtered", "First", "Temporarily absent", "Second"])
  assert.deepEqual(moveDiscussionGroup(order, visible, "First", "Second"),
    ["Second", "Filtered", "First", "Temporarily absent", "Third"])
  assert.equal(moveDiscussionGroup(order, visible, "First", "Filtered"), null)
  assert.equal(moveDiscussionGroup(order, visible, "First", "First"), null)
})


test("sorts rows independently of manual heading order and uses the visible title", () => {
  const data = [
    ...rows.filter(row => row.type === "chat").map(row => row.id === "hud" ? { ...row, lastActivity: "2026-11-23T00:00:00Z" } :
      row.id === "icons" ? { ...row, createdAt: "2026-11-21T00:00:00Z" } : row),
    make("other", "Another topic"),
  ]
  const sort: DiscussionGroupingSort = { mode: "manual", manualOrder: ["Leaf", "Lands of Lore"], discussionMode: "alphabetical" }
  const rowOrder = (options: DiscussionGroupingSort) =>
    discussionSections(data, view, options).map(section => [section.name, section.items.map(row => row.id)])

  assert.deepEqual(rowOrder(sort).slice(0, 2), [
    ["Leaf", ["triage", "cloud"]],
    ["Lands of Lore", ["icons", "hud"]],
  ])
  assert.deepEqual(rowOrder({ ...sort, refineTitles: false }).slice(0, 2), [
    ["Leaf", ["cloud", "triage"]],
    ["Lands of Lore", ["hud", "icons"]],
  ])
  assert.deepEqual(rowOrder(sort).at(-1), [null, ["other", "roleplay"]])
  assert.deepEqual(rowOrder({ ...sort, discussionMode: "activity" })[1], ["Lands of Lore", ["hud", "icons"]])
  assert.deepEqual(rowOrder({ ...sort, discussionMode: "newest" })[1], ["Lands of Lore", ["icons", "hud"]])
  assert.deepEqual(rowOrder({ ...sort, discussionMode: "oldest" })[1], ["Lands of Lore", ["hud", "icons"]])
  assert.equal(getAdjacentSidebarDiscussion(data, "cloud", 1, null, view, [], sort)?.id, "icons")
  assert.equal(getAdjacentSidebarDiscussion(data, "cloud", 1, null, view, ["Lands of Lore"], sort)?.id, "other")
  assert.equal(getAdjacentSidebarDiscussion(data, "cloud", 1, null, null, [], sort)?.id, "other")
})

test("a single failed grouping pass retries quietly after the server backoff", async () => {
  const { groupingAutoRetryDelayMs, groupingFailureVisible, GROUPING_MAX_AUTO_RETRIES } = await import("./discussion-grouping.ts")
  assert.equal(groupingFailureVisible(0), false)
  assert.equal(groupingFailureVisible(1), false)
  assert.equal(groupingFailureVisible(2), true)
  assert.equal(groupingAutoRetryDelayMs(0, 120), null)
  assert.equal(groupingAutoRetryDelayMs(1, 120), 121_000)
  assert.equal(groupingAutoRetryDelayMs(1, 0), 6_000)
  assert.equal(groupingAutoRetryDelayMs(GROUPING_MAX_AUTO_RETRIES, 120), 121_000)
  assert.equal(groupingAutoRetryDelayMs(GROUPING_MAX_AUTO_RETRIES + 1, 120), null)
})

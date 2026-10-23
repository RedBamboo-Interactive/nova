import { test } from "node:test"
import assert from "node:assert/strict"
import { createDiscussionTitleResolver, discussionDisplayTitle, type DisplayTitleSubject } from "./discussion-display-title.ts"

const NOVA = "a7b059ac-f678-8b58-819b-1bcf3acb22db"
const zurich = { timeZone: "Europe/Zurich", now: new Date("2026-10-23T12:00:00Z") }

function live(id: string, createdAt: string, extra: Partial<DisplayTitleSubject> = {}): DisplayTitleSubject {
  return { id, title: null, type: "live", createdAt, agentId: NOVA, ...extra }
}

test("an untitled Live is named by its start date in the viewer's locale", () => {
  const discussion = live("780e4aed", "2026-10-22T03:30:14Z")
  assert.equal(discussionDisplayTitle(discussion, [], { ...zurich, locale: "fr" }), "Live · 22 oct.")
  assert.equal(discussionDisplayTitle(discussion, [], { ...zurich, locale: "en-GB" }), "Live · 22 Oct")
  assert.equal(discussionDisplayTitle(discussion, [], { ...zurich, locale: "en-US" }), "Live · Oct 22")
})

test("the start date is the viewer's local day, not the UTC day", () => {
  const lateEvening = live("8f88b0e2", "2026-08-17T23:45:00Z")
  assert.equal(discussionDisplayTitle(lateEvening, [], { ...zurich, locale: "en-GB" }), "Live · 18 Aug")
  assert.equal(discussionDisplayTitle(lateEvening, [], { ...zurich, locale: "en-GB", timeZone: "UTC" }), "Live · 17 Aug")
})

test("a Live from another year includes the year", () => {
  const discussion = live("old", "2025-12-31T09:00:00Z")
  assert.equal(discussionDisplayTitle(discussion, [], { ...zurich, locale: "fr" }), "Live · 31 déc. 2025")
  assert.equal(discussionDisplayTitle(discussion, [], { ...zurich, locale: "en-GB" }), "Live · 31 Dec 2025")
})

test("Lives that started on the same local day are told apart by their start time", () => {
  const rotated = live("12f2f6c1", "2026-10-08T03:30:00Z")
  const refreshed = live("3b71ed33", "2026-10-08T20:37:00Z")
  const nextDay = live("next", "2026-10-09T03:30:00Z")
  const peers = [refreshed, rotated, nextDay]

  const fr = createDiscussionTitleResolver(peers, { ...zurich, locale: "fr" })
  assert.equal(fr(rotated), "Live · 8 oct., 05:30")
  assert.equal(fr(refreshed), "Live · 8 oct., 22:37")
  assert.equal(fr(nextDay), "Live · 9 oct.")

  const en = createDiscussionTitleResolver(peers, { ...zurich, locale: "en-US" })
  assert.equal(en(rotated), "Live · Oct 8, 5:30 AM")
  assert.equal(en(refreshed), "Live · Oct 8, 10:37 PM")
})

test("a discussion outside the peer list still sees a same-day collision", () => {
  const peers = [live("a", "2026-10-08T03:30:00Z")]
  assert.equal(
    discussionDisplayTitle(live("b", "2026-10-08T20:37:00Z"), peers, { ...zurich, locale: "en-GB" }),
    "Live · 8 Oct, 22:37",
  )
})

test("same-day Lives of different Agents, or renamed ones, are not collisions", () => {
  const resolve = createDiscussionTitleResolver([
    live("nova", "2026-10-08T03:30:00Z"),
    live("leni", "2026-10-08T04:00:00Z", { agentId: "leni" }),
    live("named", "2026-10-08T20:37:00Z", { title: "Movie night" }),
  ], { ...zurich, locale: "en-GB" })
  assert.equal(resolve(live("nova", "2026-10-08T03:30:00Z")), "Live · 8 Oct")
})

test("a renamed Live keeps its title", () => {
  const renamed = live("97370104", "2026-09-10T06:41:00Z", { title: "Saturday morning check-in" })
  assert.equal(discussionDisplayTitle(renamed, [renamed], { ...zurich, locale: "fr" }), "Saturday morning check-in")
})

test("a blank title counts as untitled", () => {
  assert.equal(discussionDisplayTitle(live("blank", "2026-10-22T03:30:00Z", { title: "  " }), [], { ...zurich, locale: "en-GB" }), "Live · 22 Oct")
})

test("ordinary untitled discussions are left to each surface's placeholder", () => {
  const chat: DisplayTitleSubject = { id: "c", title: null, type: "chat", createdAt: "2026-10-22T08:00:00Z", agentId: NOVA }
  const heartbeat: DisplayTitleSubject = { ...chat, id: "h", type: "heartbeat" }
  assert.equal(discussionDisplayTitle(chat, [chat], zurich), null)
  assert.equal(discussionDisplayTitle(heartbeat, [heartbeat], zurich), null)
  assert.equal(discussionDisplayTitle({ ...chat, title: "Fix the cache" }, [], zurich), "Fix the cache")
})

test("a Live with an unreadable start date falls back to the bare label", () => {
  assert.equal(discussionDisplayTitle(live("x", "not a date"), [], zurich), "Live")
})

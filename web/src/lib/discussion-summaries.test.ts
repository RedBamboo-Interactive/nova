import test from "node:test"
import assert from "node:assert/strict"
import {
  currentDiscussionSummary, summaryDetails, summaryPresentation, waitingDiscussionId,
  type DiscussionSummary, type DiscussionSummaryView,
} from "./discussion-summaries.ts"
import type { DiscussionInfo } from "./types.ts"

const summary = (text: Partial<DiscussionSummary["text"]>): DiscussionSummary => ({
  version: "v1",
  text: { status: "waiting", label: "Rollout switches", overview: "Two switches wait on Orbit.", nextStep: null, userAction: null, ...text },
  source: "fast",
  updatedAt: "2026-11-28T09:04:00Z",
  sessionId: "s-a",
  basisActivity: "2026-11-28T09:00:00Z",
})

const view = (s: DiscussionSummary | null, extra: Partial<DiscussionSummaryView> = {}): DiscussionSummaryView => ({
  discussionId: "a", enabled: true, override: null, version: "x", stale: false, summary: s, ...extra,
})

test("only Needs you asks for attention; the others recede", () => {
  const attention = Object.entries(summaryPresentation).filter(([, p]) => p.attention).map(([status]) => status)
  assert.deepEqual(attention, ["needs_you"])
  assert.equal(summaryPresentation.needs_you.tone, "text-primary")
  assert.equal(summaryPresentation.complete.tone, "text-accent-teal")
  for (const status of ["waiting", "in_progress"] as const) assert.equal(summaryPresentation[status].tone, "text-text-muted")
  assert.equal(summaryPresentation.unclear.tone, "text-text-disabled")
  // No status reuses the old loud gold for waiting.
  assert.ok(Object.values(summaryPresentation).every(p => !p.tone.includes("gold")))
})

test("old records without waitingOn read as waiting on nothing", () => {
  const old = summary({})
  delete (old.text as { waitingOn?: unknown }).waitingOn
  assert.equal(waitingDiscussionId(old), null)
  assert.equal(summaryDetails(old).waitingOnLabel, null)
  assert.equal(waitingDiscussionId(summary({ waitingOn: "discussion:orbit-b" })), "orbit-b")
  assert.equal(waitingDiscussionId(summary({ waitingOn: "external" })), null)
})

test("a resolved chain says what to do and where", () => {
  const resolved = summary({ status: "needs_you", userAction: "Decide on the nightly sync", waitingOn: "discussion:orbit-b" })
  const details = summaryDetails(resolved, view(resolved, {
    via: { discussionId: "orbit-b", title: "Orbit", label: "On/off decisions", userAction: "Decide on the nightly sync", updatedAt: "2026-11-28T09:10:00Z" },
  }), id => id === "orbit-b" ? "Orbit (refined)" : null)
  assert.equal(details.statusLabel, "Needs you")
  assert.equal(details.userAction, "Decide on the nightly sync")
  assert.deepEqual(details.actionIn, { discussionId: "orbit-b", title: "Orbit (refined)" })
  assert.equal(details.waitingOnLabel, null)
})

test("the action section exists only when there is something to do", () => {
  assert.equal(summaryDetails(summary({ status: "complete" })).userAction, null)
  assert.equal(summaryDetails(summary({ status: "waiting", waitingOn: "discussion:build-9" }), null, () => "Build").waitingOnLabel, "Build")
  assert.equal(summaryDetails(summary({ status: "waiting", waitingOn: "external" })).waitingOnLabel, "Someone outside Nova")
  // A via without needs_you (stale client state) is ignored.
  const waiting = summary({ status: "waiting", waitingOn: "discussion:b" })
  assert.equal(summaryDetails(waiting, view(waiting, { via: { discussionId: "b", title: "B", label: "B", userAction: "x", updatedAt: "" } })).actionIn, null)
})

test("nothing is shown without a current summary", () => {
  const discussion = { id: "a", status: "idle", sessionId: "s-a", lastActivity: "2026-11-28T09:00:00Z", confidential: false } as DiscussionInfo
  const s = summary({ status: "needs_you", userAction: "Approve it" })
  assert.equal(currentDiscussionSummary(discussion, view(s)), s)
  assert.equal(currentDiscussionSummary({ ...discussion, status: "thinking" }, view(s)), null)
  assert.equal(currentDiscussionSummary(discussion, view(s, { stale: true })), null)
  assert.equal(currentDiscussionSummary({ ...discussion, sessionId: "other" }, view(s)), null)
  assert.equal(currentDiscussionSummary(discussion, view(null)), null)
})

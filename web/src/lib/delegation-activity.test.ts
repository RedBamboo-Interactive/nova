import assert from "node:assert/strict"
import test from "node:test"
import { delegationActivityLabel, invalidatesDelegationActivity, unavailableDelegationActivity } from "./delegation-activity.ts"
import type { DiscussionDelegationActivity } from "./types.ts"

function activity(...statuses: ("running" | "queued" | "starting")[]): DiscussionDelegationActivity {
  return { ongoingCount: statuses.length, available: true, unknownCount: 0, linkedSessionIds: statuses.map((_, i) => `session-${i}`), sessions: statuses.map((status, index) => ({
    sessionId: `session-${index}`, title: null, repositoryId: null, repository: null, status, available: true,
  })) }
}

test("count and lifecycle labels distinguish queued work from running and mixed activity", () => {
  assert.equal(delegationActivityLabel(activity("running")), "1 delegation running…")
  assert.equal(delegationActivityLabel(activity("running", "running")), "2 delegations running…")
  assert.equal(delegationActivityLabel(activity("queued")), "1 delegation queued…")
  assert.equal(delegationActivityLabel(activity("starting")), "1 delegation starting…")
  assert.equal(delegationActivityLabel(activity("queued", "running")), "2 delegations ongoing…")
})

test("outage preserves the distinct authoritative sessions and count but stops claiming running", () => {
  const original = { parent: activity("running", "queued"), finished: activity() }
  const unavailable = unavailableDelegationActivity(original)
  assert.equal(unavailable.parent!.ongoingCount, 2)
  assert.equal(unavailable.parent!.available, false)
  assert.deepEqual(unavailable.parent!.sessions.map(session => session.status), ["unavailable", "unavailable"])
  assert.equal(delegationActivityLabel(unavailable.parent!), "2 delegations · status unavailable")
  assert.equal(unavailable.finished!.ongoingCount, 0)
  assert.deepEqual(unavailable.finished!.sessions, [])
  assert.equal(original.parent.sessions[0]!.status, "running")
})

test("only linked session lifecycles and originating discussion changes invalidate activity", () => {
  const linked = new Set(["worker"])
  const discussions = new Set(["parent"])
  for (const type of ["session.updated", "session.input-queue.updated", "ai-session.changed"]) {
    assert.equal(invalidatesDelegationActivity(type, { id: "worker", sessionId: "worker" }, linked, discussions), true)
    assert.equal(invalidatesDelegationActivity(type, { id: "unrelated", sessionId: "unrelated" }, linked, discussions), false)
  }
  assert.equal(invalidatesDelegationActivity("discussion.event", { discussionId: "parent", source: "delegation" }, linked, discussions), true)
  assert.equal(invalidatesDelegationActivity("discussion.event", { discussionId: "parent", source: "delegate:worker", status: "Idle" }, linked, discussions), true)
  assert.equal(invalidatesDelegationActivity("discussion.event", { discussionId: "parent", source: "weather" }, linked, discussions), false)
  assert.equal(invalidatesDelegationActivity("discussion.changed", { discussionId: "other-private" }, linked, discussions), false)
  assert.equal(invalidatesDelegationActivity("session.message", { sessionId: "worker" }, linked, discussions), false)
})

import { test } from "node:test"
import assert from "node:assert/strict"
import { ORCHESTRATION_LABELS, agentsWithHeartbeat, behaviourMarks, effectiveBehaviours, orchestrationState } from "./discussion-behaviours.ts"
import type { DiscussionInfo } from "./types.ts"

function discussion(overrides: Partial<DiscussionInfo> = {}): DiscussionInfo {
  return {
    id: "chat",
    entityId: "chat-entity",
    title: null,
    sessionId: "chat-session",
    status: "idle",
    type: "chat",
    createdAt: "2026-11-24T09:00:00.000Z",
    lastActivity: "2026-11-24T09:01:00.000Z",
    messageCount: 1,
    agentId: "nova",
    ...overrides,
  }
}

test("an ordinary discussion has no marks", () => {
  assert.deepEqual(behaviourMarks(discussion(), true), { orchestrated: false, orchestration: null, liveTimeline: false, confidential: false })
})

test("each behaviour switched on is marked", () => {
  const marks = behaviourMarks(discussion({ orchestrated: true, liveTimeline: true, orchestration: { dueAt: null, chain: 2 } }), true)
  assert.deepEqual(marks, { orchestrated: true, orchestration: "ongoing", liveTimeline: true, confidential: false })
})

test("the heartbeat is ongoing until settled, whether or not a check is due", () => {
  const due = discussion({ orchestrated: true, orchestration: { state: "ongoing", dueAt: "2026-11-24T09:04:00.000Z", chain: 0 } })
  assert.equal(behaviourMarks(due, true).orchestration, "ongoing")
  assert.equal(behaviourMarks({ ...due, orchestration: { ...due.orchestration!, dueAt: null } }, true).orchestration, "ongoing")
  assert.equal(behaviourMarks({ ...due, orchestration: null }, true).orchestration, "ongoing")
  const settled = { ...due, orchestration: { state: "settled" as const, dueAt: null, chain: 0, settledReason: "needs_you" as const } }
  assert.equal(behaviourMarks(settled, true).orchestration, "settled")
  assert.equal(orchestrationState(settled), "settled")
  // An older server sends no state: ongoing.
  assert.equal(orchestrationState(discussion({ orchestrated: true, orchestration: { dueAt: null, chain: 1 } })), "ongoing")
  assert.deepEqual(ORCHESTRATION_LABELS, { ongoing: "Orchestrated · ongoing", settled: "Orchestrated · settled until you write" })
})

test("Orchestrated shows off while it can't run, as in the Info modal", () => {
  const marked = discussion({ orchestrated: true, orchestration: { dueAt: "2026-11-24T09:04:00.000Z", chain: 0 } })
  assert.deepEqual(behaviourMarks(marked, false), { orchestrated: false, orchestration: null, liveTimeline: false, confidential: false })
  assert.deepEqual(behaviourMarks({ ...marked, confidential: true }, true), { orchestrated: false, orchestration: null, liveTimeline: false, confidential: true })
})

test("the intrinsic LIVE timeline of LIVE and Heartbeat discussions isn't marked", () => {
  for (const type of ["live", "heartbeat"] as const) {
    const d = discussion({ type })
    assert.equal(effectiveBehaviours(d, true).liveTimeline, true)
    assert.equal(behaviourMarks(d, true).liveTimeline, false)
    assert.equal(behaviourMarks({ ...d, orchestrated: true }, true).orchestrated, false)
  }
})

test("only an open Heartbeat discussion counts for its Agent", () => {
  const agents = agentsWithHeartbeat([
    discussion({ id: "h1", type: "heartbeat", agentId: "nova" }),
    discussion({ id: "h2", type: "heartbeat", agentId: "ada", status: "archived" }),
    discussion({ id: "c1", agentId: "zed" }),
  ])
  assert.deepEqual([...agents], ["nova"])
})

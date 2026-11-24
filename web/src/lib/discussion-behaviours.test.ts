import { test } from "node:test"
import assert from "node:assert/strict"
import { agentsWithHeartbeat, behaviourMarks, effectiveBehaviours } from "./discussion-behaviours.ts"
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
  assert.deepEqual(behaviourMarks(discussion(), true), { orchestrated: false, armed: false, liveTimeline: false, confidential: false })
})

test("each behaviour switched on is marked", () => {
  const marks = behaviourMarks(discussion({ orchestrated: true, liveTimeline: true, orchestration: { dueAt: null, chain: 2 } }), true)
  assert.deepEqual(marks, { orchestrated: true, armed: false, liveTimeline: true, confidential: false })
})

test("the heartbeat is armed only while the Heartbeat's check is due", () => {
  const due = discussion({ orchestrated: true, orchestration: { dueAt: "2026-11-24T09:04:00.000Z", chain: 0 } })
  assert.equal(behaviourMarks(due, true).armed, true)
  assert.equal(behaviourMarks({ ...due, orchestration: null }, true).armed, false)
})

test("Orchestrated shows off while it can't run, as in the Info modal", () => {
  const marked = discussion({ orchestrated: true, orchestration: { dueAt: "2026-11-24T09:04:00.000Z", chain: 0 } })
  assert.deepEqual(behaviourMarks(marked, false), { orchestrated: false, armed: false, liveTimeline: false, confidential: false })
  assert.deepEqual(behaviourMarks({ ...marked, confidential: true }, true), { orchestrated: false, armed: false, liveTimeline: false, confidential: true })
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
